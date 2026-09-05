// Minimal local TCP bridge: newline-delimited JSON, one line per message.
// Deliberately hand-rolled instead of using a JSON library, since every
// message here has exactly one string field (a persistence id, which is
// always alphanumeric/underscore/hyphen -- no escaping concerns).
//
// Protocol:
//   mod -> python:  {"type":"check","id":"ps_..."}
//   python -> mod:  {"type":"apply","id":"ps_..."}
//   mod -> python:  {"type":"death"}
//   python -> mod:  {"type":"kill_player"}
//
// The mod is the TCP *server* (listens on 127.0.0.1:24242); the Python
// client is the one that connects to it. This means the mod doesn't need
// to know anything about the Archipelago server address -- that's still
// entirely the Python side's responsibility.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ConstanceAP
{
    internal class ConBridgeServer
    {
        private const int Port = 24242;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        private TcpClient _client;
        private StreamWriter _writer;
        private StreamReader _reader;
        private readonly object _writeLock = new object();

        // Real bug, found by real testing: SendCheck used to silently
        // drop a check entirely if there was no connected client at that
        // exact moment (or if the write itself threw, e.g. a connection
        // that just dropped) -- no queue, no retry. Since check messages
        // (unlike apply requests) don't have any equivalent "already
        // applied" persisted state on the Python side to naturally
        // recover from this, a dropped check could stay lost until the
        // mod's own ReportAlreadyTrueLocations happened to re-derive it
        // on a later restart. Queuing here and flushing on the next
        // client connection fixes this at the real source.
        private readonly Queue<string> _pendingChecks = new Queue<string>();

        // Filler quantities are now decided client-side
        // and sent along with the grant (see the "amount" field on
        // apply_filler), so the number announced in the AP message is
        // guaranteed to be the number actually granted. That needed one
        // more slot than the old 3-tuple had; every other command simply
        // leaves Item4 null.
        private readonly Queue<Tuple<string, string, string, string>> _incomingCommands = new Queue<Tuple<string, string, string, string>>();
        private readonly object _incomingLock = new object();

        public volatile bool IsConnected;

        public ConBridgeServer(Plugin plugin)
        {
        }

        public void Start()
        {
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _client?.Close(); } catch { }
        }

        private void AcceptLoop()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                Plugin.Log.LogInfo("Bridge server listening on 127.0.0.1:" + Port);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not start bridge server: " + e);
                return;
            }

            while (_running)
            {
                try
                {
                    TcpClient incoming = _listener.AcceptTcpClient(); // blocks
                    HandleClient(incoming);
                }
                catch (Exception e)
                {
                    if (_running)
                        Plugin.Log.LogWarning("Bridge accept error: " + e);
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            lock (_writeLock)
            {
                try { _client?.Close(); } catch { }
                _client = client;
                _reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                _writer = new StreamWriter(client.GetStream(), Encoding.UTF8) { AutoFlush = true };

                if (_pendingChecks.Count > 0)
                {
                    Plugin.Log.LogInfo("Flushing " + _pendingChecks.Count + " queued check(s) now that a client is connected.");
                    while (_pendingChecks.Count > 0)
                    {
                        string pid = _pendingChecks.Dequeue();
                        try
                        {
                            _writer.WriteLine("{\"type\":\"check\",\"id\":\"" + pid + "\"}");
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning("Failed to flush queued check '" + pid + "', re-queuing: " + e);
                            _pendingChecks.Enqueue(pid);
                            break; // connection is bad again -- stop trying, wait for the next one
                        }
                    }
                }
            }
            IsConnected = true;
            Plugin.Log.LogInfo("Bridge client connected.");

            try
            {
                string line;
                while (_running && (line = _reader.ReadLine()) != null)
                {
                    string id = ExtractIdIfApplyCommand(line);
                    if (id != null)
                    {
                        // Real fix, for real testing: repeatable grants
                        // (Paint Flask/Heart Piece/Eraser) now carry an
                        // optional unique instance key, letting the mod
                        // itself recognize "I've already processed this
                        // exact grant" regardless of how many times
                        // Python resends the apply message -- see
                        // ApplyItem/GrantBankedCounter-style methods for
                        // the matching idempotency check. Null for every
                        // other, non-repeatable item.
                        string instanceKey = ExtractField(line, "instance_key");
                        lock (_incomingLock)
                        {
                            _incomingCommands.Enqueue(Tuple.Create("apply", id, instanceKey, (string)null));
                        }
                        continue;
                    }

                    string recordPositionId = ExtractIdIfRecordPositionCommand(line);
                    if (recordPositionId != null)
                    {
                        lock (_incomingLock)
                        {
                            _incomingCommands.Enqueue(Tuple.Create("record_position", recordPositionId, (string)null, (string)null));
                        }
                        continue;
                    }

                    string fillerName = ExtractNameIfFillerCommand(line);
                    if (fillerName != null)
                    {
                        string fillerInstanceKey = ExtractField(line, "instance_key");
                        // Unquoted numeric field, so it needs its own
                        // extractor rather than ExtractField (which reads
                        // quoted string values). Absent for amount-less
                        // filler (the three traps) and for any older
                        // client that doesn't send it -- null there means
                        // "mod, roll your own", preserving the previous
                        // behaviour exactly.
                        string fillerAmount = ExtractNumberField(line, "amount");
                        lock (_incomingLock)
                        {
                            _incomingCommands.Enqueue(Tuple.Create("apply_filler", fillerName, fillerInstanceKey, fillerAmount));
                        }
                        continue;
                    }

                    if (IsKillPlayerCommand(line))
                    {
                        lock (_incomingLock)
                        {
                            _incomingCommands.Enqueue(Tuple.Create("kill_player", (string)null, (string)null, (string)null));
                        }
                        continue;
                    }

                    string notificationText = ExtractTextIfNotificationCommand(line);
                    if (notificationText != null)
                    {
                        lock (_incomingLock)
                        {
                            _incomingCommands.Enqueue(Tuple.Create("notification", notificationText, (string)null, (string)null));
                        }
                        continue;
                    }

                    // Live logic-state overlay for the map
                    // icons. This one message type is a real, nested JSON
                    // array (unlike everything else here, which is one flat
                    // string field) -- deliberately still hand-detected
                    // here (simple substring check) rather than adding a
                    // dependency on this file, but the raw line itself gets
                    // passed through whole and parsed properly downstream
                    // with Newtonsoft.Json (already a real dependency of
                    // this game, referenced directly).
                    if (line.IndexOf("\"location_states\"", StringComparison.Ordinal) >= 0)
                    {
                        Plugin.Log.LogInfo("ConBridgeServer: location_states message received, queuing for main thread.");
                        lock (_incomingLock)
                        {
                            _incomingCommands.Enqueue(Tuple.Create("location_states", line, (string)null, (string)null));
                        }
                        continue;
                    }

                    // Per-category randomize toggles.
                    // Same "detect type, pass the raw line through whole"
                    // pattern as location_states above -- multiple
                    // boolean fields, not a single simple value like
                    // apply/record_position.
                    if (line.IndexOf("\"settings\"", StringComparison.Ordinal) >= 0)
                    {
                        Plugin.Log.LogInfo("ConBridgeServer: settings message received, queuing for main thread.");
                        lock (_incomingLock)
                        {
                            _incomingCommands.Enqueue(Tuple.Create("settings", line, (string)null, (string)null));
                        }
                        continue;
                    }

                    // Shrine warp. Same raw-line-passthrough
                    // pattern as settings/location_states above.
                    if (line.IndexOf("\"warp\"", StringComparison.Ordinal) >= 0)
                    {
                        Plugin.Log.LogInfo("ConBridgeServer: warp message received, queuing for main thread.");
                        lock (_incomingLock)
                        {
                            _incomingCommands.Enqueue(Tuple.Create("warp", line, (string)null, (string)null));
                        }
                        continue;
                    }

                    // Warp by recorded name -- the actual
                    // command !warp <name> sends this as
                    // {"type":"warp_by_name","name":"..."}. Simple field
                    // extraction rather than a full JSON parse, same
                    // pattern as ExtractIdIfRecordPositionCommand
                    // elsewhere in this file.
                    if (line.IndexOf("\"warp_by_name\"", StringComparison.Ordinal) >= 0)
                    {
                        string warpName = ExtractField(line, "name");
                        if (warpName != null)
                        {
                            lock (_incomingLock)
                            {
                                _incomingCommands.Enqueue(Tuple.Create("warp_by_name", warpName, (string)null, (string)null));
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Bridge client read error: " + e);
            }
            finally
            {
                IsConnected = false;
                Plugin.Log.LogInfo("Bridge client disconnected.");
            }
        }

        /// Very small hand-rolled parser: only understands
        /// {"type":"apply","id":"<value>"} and returns the id, or null.
        private static string ExtractIdIfApplyCommand(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            if (line.IndexOf("\"apply\"", StringComparison.Ordinal) < 0) return null;

            return ExtractField(line, "id");
        }

        /// Understands {"type":"record_position","id":"<value>"} and
        /// returns the id, or null. Direct request: the !recordpos client
        /// command, for manually filling in map-icon position data for
        /// locations the normal check flow never recorded.
        private static string ExtractIdIfRecordPositionCommand(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            if (line.IndexOf("\"record_position\"", StringComparison.Ordinal) < 0) return null;

            return ExtractField(line, "id");
        }

        /// Understands {"type":"apply_filler","name":"<value>"} and
        /// returns the item name, or null.
        private static string ExtractNameIfFillerCommand(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            if (line.IndexOf("\"apply_filler\"", StringComparison.Ordinal) < 0) return null;

            return ExtractField(line, "name");
        }

        /// Reads an UNQUOTED numeric JSON field (e.g. "amount":75) and
        /// returns its raw digits, or null if the field isn't present.
        /// Deliberately separate from ExtractField, which only handles
        /// quoted string values.
        private static string ExtractNumberField(string line, string fieldName)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int keyIndex = line.IndexOf("\"" + fieldName + "\"", StringComparison.Ordinal);
            if (keyIndex < 0) return null;
            int colonIndex = line.IndexOf(':', keyIndex);
            if (colonIndex < 0) return null;

            int i = colonIndex + 1;
            while (i < line.Length && line[i] == ' ') i++;
            int start = i;
            if (i < line.Length && (line[i] == '-' || line[i] == '+')) i++;
            while (i < line.Length && char.IsDigit(line[i])) i++;
            if (i == start) return null;
            return line.Substring(start, i - start);
        }

        /// null / unparseable both mean "no explicit amount was sent",
        /// which ApplyFiller treats as "roll one yourself".
        private static int? ParseAmount(string raw)
        {
            int parsed;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out parsed) && parsed > 0) return parsed;
            return null;
        }

        private static bool IsKillPlayerCommand(string line)
        {
            return !string.IsNullOrEmpty(line) && line.IndexOf("\"kill_player\"", StringComparison.Ordinal) >= 0;
        }

        /// Understands {"type":"notification","text":"<value>"} and
        /// returns the unescaped text, or null. Needs real JSON string
        /// unescaping (\" and \\) unlike the other simple extractors above,
        /// since rich-text notification content is far more likely to
        /// contain characters that need escaping than a plain id or name.
        private static string ExtractTextIfNotificationCommand(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            if (line.IndexOf("\"notification\"", StringComparison.Ordinal) < 0) return null;

            int keyIndex = line.IndexOf("\"text\"", StringComparison.Ordinal);
            if (keyIndex < 0) return null;
            int colonIndex = line.IndexOf(':', keyIndex);
            if (colonIndex < 0) return null;
            int firstQuote = line.IndexOf('"', colonIndex + 1);
            if (firstQuote < 0) return null;

            var sb = new System.Text.StringBuilder();
            int i = firstQuote + 1;
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length)
                {
                    char next = line[i + 1];
                    if (next == '"') { sb.Append('"'); i += 2; continue; }
                    if (next == '\\') { sb.Append('\\'); i += 2; continue; }
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (c == '"') break; // unescaped quote -- end of string
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static string ExtractField(string line, string fieldName)
        {
            int keyIndex = line.IndexOf("\"" + fieldName + "\"", StringComparison.Ordinal);
            if (keyIndex < 0) return null;

            int colonIndex = line.IndexOf(':', keyIndex);
            if (colonIndex < 0) return null;

            int firstQuote = line.IndexOf('"', colonIndex + 1);
            if (firstQuote < 0) return null;
            int secondQuote = line.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0) return null;

            return line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }

        public void SendCheck(string persistenceId)
        {
            lock (_writeLock)
            {
                if (_writer == null)
                {
                    _pendingChecks.Enqueue(persistenceId);
                    Plugin.Log.LogInfo("No bridge client connected yet -- queuing check '" + persistenceId + "' to send once connected.");
                    return;
                }
                try
                {
                    _writer.WriteLine("{\"type\":\"check\",\"id\":\"" + persistenceId + "\"}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Failed to send check to bridge client, queuing for retry: " + e);
                    _pendingChecks.Enqueue(persistenceId);
                }
            }
        }

        public void SendDeath()
        {
            lock (_writeLock)
            {
                if (_writer == null) return;
                try
                {
                    _writer.WriteLine("{\"type\":\"death\"}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Failed to send death to bridge client: " + e);
                }
            }
        }

        // The mod's own progress messages (e.g. "Paint
        // Flask found (3/12)") were only ever showing in the mod's own
        // in-game overlay (UiLog/_uiLog), never in the actual Archipelago
        // client's own log/chat window -- which is what "in the
        // archipelago log" always meant. There was no existing mechanism
        // for the mod to send text TO the Python client at all -- every
        // existing message type (check, death) is one-way, mod-initiated
        // but never carrying arbitrary display text; "notification" goes
        // the OPPOSITE direction (Python forwards AP's own "Received: X"
        // text to the mod for in-game display, not the mod sending
        // anything back). Best-effort, not queued like SendCheck -- a
        // missed progress message isn't worth holding up or retrying,
        // unlike a real check report.
        public void SendClientMessage(string text)
        {
            lock (_writeLock)
            {
                if (_writer == null) return;
                try
                {
                    string escaped = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    _writer.WriteLine("{\"type\":\"client_message\",\"text\":\"" + escaped + "\"}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Failed to send client message to bridge client: " + e);
                }
            }
        }

        /// Called from Unity's main thread (via Plugin.Update) to safely
        /// apply anything the background thread has queued up.
        public void PumpMainThreadWork(Plugin plugin)
        {
            while (true)
            {
                Tuple<string, string, string, string> command = null;
                lock (_incomingLock)
                {
                    if (_incomingCommands.Count > 0)
                        command = _incomingCommands.Dequeue();
                }
                if (command == null) break;

                if (command.Item1 == "apply")
                    plugin.ApplyItem(command.Item2, command.Item3);
                else if (command.Item1 == "apply_filler")
                    plugin.ApplyFiller(command.Item2, command.Item3, ParseAmount(command.Item4));
                else if (command.Item1 == "kill_player")
                    plugin.KillLocalPlayer();
                else if (command.Item1 == "notification")
                    plugin.AddNotification(command.Item2);
                else if (command.Item1 == "location_states")
                    MapIconInjector.ApplyLocationStates(command.Item2);
                else if (command.Item1 == "settings")
                    Plugin.ApplySettingsFromJson(command.Item2);
            }
        }
    }
}
