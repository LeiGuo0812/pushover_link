using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexPushover
{
    public sealed class SendResult
    {
        public int Http;
        public int? Status;
        public bool Network;
        public bool Malformed;
    }

    public static class Core
    {
        static string root;
        static readonly object cycleLock = new object();
        static readonly object logLock = new object();
        static volatile bool busy;
        static volatile bool pause;
        public static bool Busy { get { return busy; } }
        public static bool Pause { get { return pause; } set { pause = value; } }
        // Injection points are used only by isolated tests; production leaves them null.
        public static Func<Dictionary<string, object>, string, string, SendResult> TestTransport;
        public static Func<Dictionary<string, object>, Dictionary<string, object>> TestMetadata;
        public static Func<long> TestClock;
        public static Action<string> TestDiagnostic;
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 2097152, RecursionLimit = 100 }; }
        static Dictionary<string, object> Obj() { return new Dictionary<string, object>(); }
        static string PathAt(string name) { return Path.Combine(root, name); }
        static string Stamp() { return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }
        static long Now() { return TestClock == null ? (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds : TestClock(); }
        static string At(long value) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(value).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }
        static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        static string S(object v) { return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }
        static bool B(object v) { return v != null && ((v is bool && (bool)v) || (!(v is bool) && S(v) != "" && S(v) != "0")); }
        static long N(object v) { try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; } }
        static Dictionary<string, object> D(object v) { return v as Dictionary<string, object> ?? Obj(); }
        static Dictionary<string, object> Copy(Dictionary<string, object> d) { return new Dictionary<string, object>(d); }
        static bool Exists(string name) { return File.Exists(PathAt(name)); }
        public const string SuggestionOption = "notify_composer_suggestions";
        public const string InputOption = "notify_input_requests";
        public static bool OptionEnabled(string key)
        { object value = Get(Optional(PathAt("notification-options.json")), key); return value is bool && (bool)value; }
        public static void SetOption(string key, bool enabled)
        {
            if (key != SuggestionOption && key != InputOption) throw new ArgumentException("Unknown notification option.");
            lock (cycleLock)
            {
                var options = ReadObject(PathAt("notification-options.json")); options[key] = enabled;
                WriteObject(PathAt("notification-options.json"), options); Log("notification_option_updated");
                if (!enabled) foreach (string file in Directory.GetFiles(PathAt("events"), "*.json"))
                {
                    string stateFile = Path.Combine(PathAt("state"), Path.GetFileName(file)); Dictionary<string, object> record, state;
                    if (!ReadQueued(file, stateFile, out record, out state)) continue;
                    string kind = S(Get(record, "notification_kind"));
                    if (kind != (key == SuggestionOption ? "composer_suggestion" : "input_request") || S(Get(state, "outcome")) == "accepted") continue;
                    state["outcome"] = "suppressed"; WriteObject(stateFile, state);
                }
            }
        }
        public static string InputEventId(string session, string turn, string call)
        { return Hash("[" + JSString(session) + "," + JSString(turn) + ",\"input_request\"," + JSString(call) + "]"); }
        static bool CategoryEnabled(Dictionary<string, object> record)
        {
            string kind = S(Get(record, "notification_kind"));
            return kind == "composer_suggestion" ? OptionEnabled(SuggestionOption) : kind != "input_request" || OptionEnabled(InputOption);
        }

        public static void Initialize(string directory)
        {
            root = Path.GetFullPath(directory);
            Directory.CreateDirectory(root);
            foreach (string d in new [] { "events", "state", "prompts", "quarantine" }) Directory.CreateDirectory(PathAt(d));
        }
        public static Dictionary<string, object> ReadObject(string path)
        {
            if (!File.Exists(path)) return Obj();
            return Serializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF')) ?? Obj();
        }
        static Dictionary<string, object> Optional(string path)
        {
            try { return ReadObject(path); } catch { return Obj(); }
        }
        public static void WriteObject(string path, Dictionary<string, object> value)
        {
            string tmp = path + "." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] bytes = Encoding.UTF8.GetBytes(Serializer().Serialize(value) + "\n");
            try
            {
                using (FileStream f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { f.Write(bytes, 0, bytes.Length); f.Flush(true); }
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(tmp, path, null);
                        else File.Move(tmp, path);
                        break;
                    }
                    catch (IOException) { if (attempt >= 9) throw; Thread.Sleep(10); }
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        public static void Log(string kind)
        {
            // Do not accept arbitrary details, exception text, credentials, or bodies.
            if (!Regex.IsMatch(kind ?? "", "^[a-z0-9_]{1,80}$")) kind = "local_event";
            lock (logLock)
            {
                try
                {
                    string file = PathAt("sender.log");
                    if (File.Exists(file) && new FileInfo(file).Length > 1048576)
                    { File.Delete(file + ".1"); File.Move(file, file + ".1"); }
                    var line = Obj(); line["time"] = Stamp(); line["kind"] = kind;
                    File.AppendAllText(file, Serializer().Serialize(line) + "\n", new UTF8Encoding(false));
                }
                catch { }
            }
        }
        static string Hash(string text)
        {
            using (SHA256 h = SHA256.Create())
            {
                byte[] value = h.ComputeHash(new UTF8Encoding(false).GetBytes(text));
                return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
            }
        }
        // JSON.stringify-compatible string escaping is essential to retain old keys.
        static string JSString(string value)
        {
            StringBuilder s = new StringBuilder("\"");
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': s.Append("\\\""); break;
                    case '\\': s.Append("\\\\"); break;
                    case '\b': s.Append("\\b"); break;
                    case '\f': s.Append("\\f"); break;
                    case '\n': s.Append("\\n"); break;
                    case '\r': s.Append("\\r"); break;
                    case '\t': s.Append("\\t"); break;
                    default:
                        if (c < 32 || (Char.IsSurrogate(c) && !(Char.IsHighSurrogate(c) && i + 1 < value.Length && Char.IsLowSurrogate(value[i + 1])) && !(Char.IsLowSurrogate(c) && i > 0 && Char.IsHighSurrogate(value[i - 1]))))
                            s.Append("\\u" + ((int)c).ToString("x4"));
                        else s.Append(c);
                        break;
                }
            }
            return s.Append('"').ToString();
        }
        public static string EventId(string session, string turn) { return Hash("[" + JSString(session) + "," + JSString(turn) + "]"); }
        public static int CharacterCount(string value)
        {
            int n = 0;
            for (int i = 0; i < value.Length; i++, n++) if (Char.IsHighSurrogate(value[i]) && i + 1 < value.Length && Char.IsLowSurrogate(value[i + 1])) i++;
            return n;
        }
        static string SliceChars(string value, int count)
        {
            int i = 0, n = 0;
            while (i < value.Length && n++ < count) { if (Char.IsHighSurrogate(value[i]) && i + 1 < value.Length && Char.IsLowSurrogate(value[i + 1])) i++; i++; }
            return value.Substring(0, i);
        }
        public static string Shorten(string value, int count, string suffix)
        { return CharacterCount(value) <= count ? value : SliceChars(value, Math.Max(0, count - CharacterCount(suffix))) + suffix; }
        static string Preview(string value)
        {
            string bounded = value.Substring(0, Math.Min(value.Length, 8192));
            if (bounded.Length != value.Length && bounded.Length > 0 && Char.IsHighSurrogate(bounded[bounded.Length - 1])) bounded = bounded.Substring(0, bounded.Length - 1);
            return SliceChars(bounded, 1025);
        }
        public static string VisibleInput(string text)
        {
            text = (text ?? "").Trim();
            const string before = "<send_user_message_question_reply>", after = "</send_user_message_question_reply>";
            if (text.StartsWith(before, StringComparison.Ordinal) && text.EndsWith(after, StringComparison.Ordinal))
            {
                try
                {
                    object[] items = Serializer().DeserializeObject(text.Substring(before.Length, text.Length - before.Length - after.Length)) as object[];
                    if (items != null && items.Length > 0)
                    {
                        List<string> answers = new List<string>();
                        foreach (object o in items) { var item = o as Dictionary<string, object>; if (!(Get(item, "answer") is string)) return text; answers.Add((string)item["answer"]); }
                        return String.Join("\n", answers.ToArray()).Trim();
                    }
                }
                catch { }
            }
            return text;
        }
        static Dictionary<string, object> Outcome(string outcome, string id)
        { var r = Obj(); r["outcome"] = outcome; if (id != null) r["id"] = id; return r; }
        static bool IsComposerSuggestionRequest(string prompt)
        {
            string text = (prompt ?? "").TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            return text.StartsWith("This is an app-generated request for a suggested next user message. The user did not write this message.", StringComparison.Ordinal) &&
                text.Contains("Your suggestion will appear in the user's empty message composer.");
        }
        static bool IsComposerSuggestionTurn(string id, string session, string turn)
        {
            var saved = Optional(Path.Combine(PathAt("prompts"), id + ".json"));
            return S(Get(saved, "session_id")) == session && S(Get(saved, "turn_id")) == turn &&
                (B(Get(saved, "composer_suggestion")) || IsComposerSuggestionRequest(S(Get(saved, "user_input_preview"))));
        }
        public static Dictionary<string, object> HandleHook(string json)
        {
            try
            {
                var e = Serializer().Deserialize<Dictionary<string, object>>(json);
                return Handle(e, "hook");
            }
            catch { Log("hook_local_error"); return Outcome("local_error", null); }
        }
        static Dictionary<string, object> Handle(Dictionary<string, object> e, string source)
        {
            if (Exists("DISABLED")) return Outcome("disabled", null);
            string kind = S(Get(e, "hook_event_name"));
            if (kind != "Stop" && kind != "UserPromptSubmit" && kind != "PreToolUse") return Outcome("ignored_event", null);
            if (B(Get(e, "agent_id")) || B(Get(e, "agent_type"))) return Outcome("ignored_subagent", null);
            string session = Get(e, "session_id") as string, turn = Get(e, "turn_id") as string;
            if (String.IsNullOrEmpty(session) || String.IsNullOrEmpty(turn) || (kind == "UserPromptSubmit" && !(Get(e, "prompt") is string)))
            { Log("missing_event_identifiers"); return Outcome("missing_identifiers_or_prompt", null); }
            string id = EventId(session, turn), now = Stamp();
            if (kind == "PreToolUse") return HandleInputRequest(e, source, session, turn, now);
            if (kind == "UserPromptSubmit")
            {
                if (IsComposerSuggestionRequest((string)e["prompt"]))
                {
                    // Keep only an exact-turn marker; the generated prompt may contain
                    // conversation context that must not appear in a notification.
                    var marker = Obj(); marker["session_id"] = session; marker["turn_id"] = turn;
                    marker["composer_suggestion"] = true; marker["captured_at"] = now;
                    WriteObject(Path.Combine(PathAt("prompts"), id + ".json"), marker);
                    bool enabled = OptionEnabled(SuggestionOption);
                    Log(enabled ? "composer_suggestion_captured" : "composer_suggestion_ignored");
                    return Outcome(enabled ? "captured_suggestion" : "ignored_suggestion", id);
                }
                string input = VisibleInput((string)e["prompt"]), preview = Preview(input);
                var p = Obj(); p["session_id"] = session; p["turn_id"] = turn; p["source"] = source;
                p["user_input_preview"] = preview; p["user_input_was_cut"] = preview.Length < input.Length; p["captured_at"] = now;
                WriteObject(Path.Combine(PathAt("prompts"), id + ".json"), p); Log("prompt_captured");
                return Outcome("captured", id);
            }
            bool suggestion = IsComposerSuggestionTurn(id, session, turn);
            if (suggestion && !OptionEnabled(SuggestionOption))
            { Log("composer_suggestion_ignored"); return Outcome("ignored_suggestion", id); }
            string reply = Get(e, "last_assistant_message") as string ?? ""; reply = reply.Trim();
            if (suggestion)
            {
                // Send only the proposed user-facing text, never the app-generated prompt.
                try { reply = Get(Serializer().Deserialize<Dictionary<string, object>>(reply), "suggestion") as string ?? ""; }
                catch { reply = ""; }
                reply = reply.Trim();
                if (reply == "") { Log("composer_suggestion_empty"); return Outcome("empty_suggestion", id); }
            }
            string replyPreview = Preview(reply);
            var record = Obj(); record["id"] = id; record["source"] = source; record["session_id"] = session; record["turn_id"] = turn;
            record["created_at"] = now; record["format_version"] = 2; record["cwd"] = S(Get(e, "cwd"));
            record["cwd"] = S(record["cwd"]).Substring(0, Math.Min(S(record["cwd"]).Length, 4096));
            record["reply_preview"] = replyPreview; record["reply_was_cut"] = replyPreview.Length < reply.Length;
            if (suggestion) record["notification_kind"] = "composer_suggestion";
            if (Get(e, "transcript_path") is string) { string t = (string)e["transcript_path"]; record["transcript_path"] = t.Substring(0, Math.Min(t.Length, 4096)); }
            var options = Optional(PathAt("notification-options.json"));
            if (!suggestion && S(Get(options, "content_mode")) == "user_input")
            {
                record["format_version"] = 3; record["user_input_captured"] = false;
                ApplyPrompt(record, id);
            }
            return Enqueue(record);
        }
        static Dictionary<string, object> HandleInputRequest(Dictionary<string, object> e, string source, string session, string turn, string now)
        {
            if (!Regex.IsMatch(S(Get(e, "tool_name")), @"^(functions\.)?request_user_input(_async)?$")) return Outcome("ignored_tool", null);
            if (!OptionEnabled(InputOption)) return Outcome("input_notifications_disabled", null);
            string call = Get(e, "tool_use_id") as string;
            if (String.IsNullOrEmpty(call)) return Outcome("missing_tool_use_id", null);
            var input = Get(e, "tool_input") as Dictionary<string, object>;
            var questions = Get(input, "questions") as IList;
            if (questions == null || questions.Count == 0) return Outcome("missing_questions", null);
            var lines = new List<string>();
            foreach (object item in questions)
            {
                var q = item as Dictionary<string, object>;
                string title = Get(q, "question") as string ?? Get(q, "title") as string;
                if (String.IsNullOrWhiteSpace(title)) continue;
                lines.Add(Preview(title.Trim()));
                var choices = Get(q, "options") as IList;
                if (choices != null) foreach (object choice in choices)
                {
                    string label = choice as string ?? Get(choice as Dictionary<string, object>, "label") as string;
                    if (!String.IsNullOrWhiteSpace(label)) lines.Add("• " + Preview(label.Trim()));
                    if (lines.Count >= 20) break;
                }
                if (lines.Count >= 20) break;
            }
            if (lines.Count == 0) return Outcome("missing_questions", null);
            string text = String.Join("\n", lines.ToArray()), preview = Preview(text);
            var record = Obj(); record["id"] = InputEventId(session, turn, call); record["session_id"] = session; record["turn_id"] = turn;
            record["tool_use_id"] = call; record["source"] = source; record["created_at"] = now; record["format_version"] = 2;
            record["notification_kind"] = "input_request"; record["cwd"] = Shorten(S(Get(e, "cwd")), 4096, "");
            record["reply_preview"] = preview; record["reply_was_cut"] = preview.Length < text.Length;
            return Enqueue(record);
        }
        static Dictionary<string, object> Enqueue(Dictionary<string, object> record)
        {
            string id = S(record["id"]);
            string target = Path.Combine(PathAt("events"), id + ".json"), tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string outcome = "queued";
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Serializer().Serialize(record));
                using (FileStream f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes, 0, bytes.Length); f.Flush(true); }
                try { File.Move(tmp, target); }
                catch (IOException) { if (File.Exists(target)) outcome = "duplicate"; else throw; }
            }
            finally { try { File.Delete(tmp); } catch { } }
            Log(outcome); return Outcome(outcome, id);
        }
        static void ApplyPrompt(Dictionary<string, object> record, string id)
        {
            var p = Optional(Path.Combine(PathAt("prompts"), id + ".json"));
            if (S(Get(p, "session_id")) == S(Get(record, "session_id")) && S(Get(p, "turn_id")) == S(Get(record, "turn_id")) && Get(p, "user_input_preview") is string)
            {
                record["user_input_preview"] = VisibleInput(S(p["user_input_preview"])); record["user_input_was_cut"] = B(Get(p, "user_input_was_cut"));
                record["user_input_captured"] = true; record["input_source"] = "user_prompt_submit";
            }
        }
        public static string EnqueueTest(string project, string input, string reply)
        {
            var e = Obj(); e["session_id"] = "exe-send-test"; e["turn_id"] = Guid.NewGuid().ToString(); e["hook_event_name"] = "UserPromptSubmit";
            e["prompt"] = input ?? ""; Handle(e, "test"); e["hook_event_name"] = "Stop"; e["cwd"] = project; e["last_assistant_message"] = reply;
            return S(Get(Handle(e, "test"), "id"));
        }
        static Dictionary<string, object> Credentials()
        {
            var c = Optional(PathAt("credentials.json"));
            string token = S(Get(c, "PUSHOVER_APP_TOKEN")).Trim(), user = S(Get(c, "PUSHOVER_USER_KEY")).Trim();
            if (!Regex.IsMatch(token, "^[A-Za-z0-9]{30}$") || !Regex.IsMatch(user, "^[A-Za-z0-9]{30}$")) return null;
            var r = Obj(); r["token"] = token; r["user"] = user; r["fingerprint"] = Hash(token + "\0" + user); return r;
        }
        public static Dictionary<string, object> Compose(Dictionary<string, object> record, Dictionary<string, object> metadata, Dictionary<string, object> options)
        {
            string cwd = S(Get(record, "cwd")).Replace('\\', '/').TrimEnd('/'); int slash = cwd.LastIndexOf('/');
            string project = S(Get(metadata, "project_name")); if (project == "") project = slash >= 0 ? cwd.Substring(slash + 1) : cwd;
            if (project == "") project = "未知项目";
            project = Shorten(Regex.Replace(project, @"\s+", " ").Trim(), 160, "…");
            string kind = S(Get(record, "notification_kind"));
            if (kind == "composer_suggestion" || kind == "input_request")
            {
                bool suggestion = kind == "composer_suggestion";
                string specialPrefix = "项目名称：" + project + (suggestion ? "\n输入框建议：\n" : "\n需要你回答：\n");
                string footer = suggestion ? "\n请在 Codex 查看并选择是否采用。" : "\n请返回 Codex 回答或选择。";
                string content = S(Get(record, "reply_preview")); int available = 1024 - CharacterCount(specialPrefix + footer);
                bool cut = B(Get(record, "reply_was_cut")) || CharacterCount(content) > available;
                if (cut) content = Shorten(content, available, "…（已截断）");
                return new Dictionary<string, object> { {"title", suggestion ? "Codex 有新的输入建议" : "Codex 需要你回答"}, {"message", specialPrefix + content + footer}, {"truncated", cut}, {"metadata_source", "cwd"} };
            }
            string middle;
            if (N(Get(record, "format_version")) == 3)
            {
                int limit = Get(options, "user_input_max_chars") == null ? 200 : (int)Math.Max(8, Math.Min(400, N(options["user_input_max_chars"])));
                string input = S(Get(record, "user_input_preview"));
                if (input == "") input = B(Get(record, "user_input_captured")) ? "（本轮用户输入为空）" : "（未捕获该轮用户输入）";
                string suffix = "…（输入已截断）";
                if (B(Get(record, "user_input_was_cut")) || CharacterCount(input) > limit) input = SliceChars(input, Math.Max(0, limit - CharacterCount(suffix))) + suffix;
                middle = "用户输入：\n" + input;
            }
            else
            {
                int limit = Get(options, "conversation_title_max_chars") == null ? 200 : (int)Math.Max(8, Math.Min(200, N(options["conversation_title_max_chars"])));
                string title = S(Get(metadata, "conversation_title")); if (title == "") title = "未能读取对话标题";
                middle = "对话标题：" + Shorten(Regex.Replace(title, @"\s+", " ").Trim(), limit, "…");
            }
            string prefix = "项目名称：" + project + "\n" + middle + "\n本轮回复：\n";
            string text = S(Get(record, "reply_preview")); if (text == "") text = "（本轮未提供可显示的回复）";
            int budget = Math.Max(0, 1024 - CharacterCount(prefix)); bool truncated = B(Get(record, "reply_was_cut")) || CharacterCount(text) > budget;
            if (truncated) text = Shorten(text, budget, "\n…（回复已截断，请在 Codex 查看完整内容）");
            var r = Obj(); r["title"] = "Codex 本轮已结束"; r["message"] = prefix + text; r["truncated"] = truncated;
            r["metadata_source"] = S(Get(metadata, "metadata_source")) == "" ? "fallback" : metadata["metadata_source"]; return r;
        }
        static string WslPath(string value)
        {
            string v = (value ?? "").Replace('\\', '/');
            return Regex.Replace(v, @"^([A-Za-z]):/", delegate(Match m) { return "/mnt/" + m.Groups[1].Value.ToLowerInvariant() + "/"; });
        }
        static string Normalize(string value) { return Regex.Replace(WslPath(value), @"^/\?/", "").TrimEnd('/').ToLowerInvariant(); }
        static string Arg(string value)
        {
            // WSL parses flags from the raw Windows command line: leave simple flags unquoted.
            // Quote only whitespace/quotes, using the Windows CRT backslash rules; no shell expansion.
            if (value.Length > 0 && !Regex.IsMatch(value, "[\\s\"]")) return value;
            StringBuilder s = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') slashes++;
                else { s.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); s.Append(c); slashes = 0; }
            }
            s.Append('\\', slashes * 2); return s.Append('"').ToString();
        }
        static bool Contains(object array, string needle)
        { var items = array as IEnumerable; if (items != null) foreach (object item in items) if (S(item) == needle) return true; return false; }
        static string FallbackProject(Dictionary<string, object> record, Dictionary<string, object> global)
        {
            var projects = D(Get(global, "local-projects"));
            var assignment = D(Get(D(Get(global, "thread-project-assignments")), S(Get(record, "session_id"))));
            string assigned = S(Get(assignment, "projectId"));
            if (assigned != "" && projects.ContainsKey(assigned)) return S(Get(D(projects[assigned]), "name"));
            if (Contains(Get(global, "projectless-thread-ids"), S(Get(record, "session_id")))) return "未关联项目";
            string cwd = Normalize(S(Get(record, "cwd"))), best = ""; int length = 0;
            foreach (object p in projects.Values)
            {
                var project = D(p); var roots = Get(project, "rootPaths") as IEnumerable;
                if (roots == null) continue;
                foreach (object r in roots)
                {
                    string normalized = Normalize(S(r));
                    if (normalized.Length > length && (cwd == normalized || cwd.StartsWith(normalized + "/", StringComparison.Ordinal)))
                    { length = normalized.Length; best = S(Get(project, "name")); }
                }
            }
            return best;
        }
        public static Dictionary<string, object> LookupMetadata(Dictionary<string, object> record)
        {
            if (TestMetadata != null) return TestMetadata(record);
            var installation = Optional(PathAt("installation.json"));
            string home = S(Get(installation, "config_home"));
            var global = home == "" ? Obj() : Optional(Path.Combine(home, ".codex-global-state.json"));
            var assignment = D(Get(D(Get(global, "thread-project-assignments")), S(Get(record, "session_id"))));
            string assignedId = S(Get(assignment, "projectId"));
            var map = D(Get(D(Get(global, "app-server-project-id-by-legacy-project-id-by-host")), "local:" + WslPath(home)));
            if (assignedId != "" && map.ContainsKey(assignedId)) assignedId = S(map[assignedId]);
            var metadata = Obj(); string executable = S(Get(installation, "metadata_wsl_executable"));
            string database = S(Get(installation, "metadata_sqlite_path"));
            if (executable != "" && database != "")
            {
                try
                {
                    string script;
                    using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexPushover.Metadata.py"))
                    { if (resource == null) throw new IOException(); using (StreamReader reader = new StreamReader(resource, Encoding.UTF8)) script = reader.ReadToEnd(); }
                    string[] args = new [] { "--distribution", S(Get(installation, "metadata_wsl_distro")), "--exec", "/usr/bin/python3", "-c", "import sys; exec(compile(sys.stdin.read(), '<codex-pushover-metadata>', 'exec'))",
                        database, S(Get(record, "session_id")), assignedId == "" ? "-" : assignedId,
                        N(Get(record, "format_version")) == 3 ? S(Get(record, "turn_id")) : "-",
                        S(Get(record, "transcript_path")) == "" ? "-" : WslPath(S(record["transcript_path"])) };
                    StringBuilder command = new StringBuilder(); foreach (string a in args) { if (command.Length > 0) command.Append(' '); command.Append(Arg(a)); }
                    using (Process child = new Process())
                    {
                        child.StartInfo = new ProcessStartInfo(executable, command.ToString()) { UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                        using (var output = new MemoryStream())
                        using (var outputDone = new ManualResetEvent(false))
                        {
                            object outputLock = new object(); bool tooLarge = false;
                            child.Start();
                            Task.Factory.StartNew(delegate {
                                try
                                {
                                    byte[] chunk = new byte[1024]; int count;
                                    while ((count = child.StandardOutput.BaseStream.Read(chunk, 0, chunk.Length)) > 0)
                                    {
                                        lock (outputLock)
                                        {
                                            if (output.Length + count > 32768) { tooLarge = true; try { child.Kill(); } catch { } break; }
                                            output.Write(chunk, 0, count);
                                        }
                                    }
                                }
                                catch { }
                                finally { try { outputDone.Set(); } catch { } }
                            });
                            Task.Factory.StartNew(delegate {
                                try { byte[] discard = new byte[1024]; while (child.StandardError.BaseStream.Read(discard, 0, discard.Length) > 0) { } }
                                catch { } // Stderr is drained, never saved or printed.
                            });
                            Task.Factory.StartNew(delegate { try { child.StandardInput.Write(script); child.StandardInput.Close(); } catch { } });
                            if (!child.WaitForExit(5500)) { try { child.Kill(); } catch { } child.WaitForExit(200); }
                            else
                            {
                                if (TestDiagnostic != null) TestDiagnostic("process_exit_" + child.ExitCode);
                                if (outputDone.WaitOne(200))
                                    lock (outputLock)
                                    { if (!tooLarge && child.ExitCode == 0 && output.Length > 0) metadata = Serializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(output.ToArray())) ?? Obj(); }
                            }
                        }
                    }
                }
                catch (Exception e) { Log("metadata_lookup_unavailable"); if (TestDiagnostic != null) TestDiagnostic(e.GetType().Name); }
            }
            string fallback = FallbackProject(record, global);
            if (fallback != "" && ((assignment.Count > 0 && !B(Get(metadata, "project_from_assignment"))) || S(Get(metadata, "project_name")) == "" || fallback == "未关联项目"))
                metadata["project_name"] = fallback;
            return metadata;
        }
        sealed class PostOperation
        {
            public volatile HttpWebRequest Request;
            public volatile bool TimedOut;
        }
        static SendResult Post(Dictionary<string, object> body, string token, string user)
        { return PostEndpoint(body, token, user, "https://api.pushover.net/1/messages.json"); }
        // A private endpoint parameter permits loopback transport tests through reflection.
        // There is no configurable production destination, public override, or saved setting.
        static SendResult PostEndpoint(Dictionary<string, object> body, string token, string user, string endpoint)
        {
            // A bounded outer wait also covers DNS implementations that do not promptly honor Abort().
            // The worker stops starting new attempts on timeout; late acceptance remains possible.
            var operation = new PostOperation();
            Task<SendResult> task = Task.Factory.StartNew(delegate { return PostInternal(body, token, user, operation, endpoint); });
            if (task.Wait(10000)) return task.Result;
            operation.TimedOut = true;
            HttpWebRequest request = operation.Request;
            if (request != null) try { request.Abort(); } catch { }
            return new SendResult { Network = true };
        }
        static SendResult PostInternal(Dictionary<string, object> body, string token, string user, PostOperation operation, string endpoint)
        {
            HttpWebRequest request = null;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                request = (HttpWebRequest)WebRequest.Create(endpoint);
                operation.Request = request;
                if (operation.TimedOut) { request.Abort(); return new SendResult { Network = true }; }
                request.Method = "POST"; request.ContentType = "application/x-www-form-urlencoded"; request.Timeout = 10000; request.ReadWriteTimeout = 10000;
                request.AllowAutoRedirect = false; request.KeepAlive = false;
                byte[] data = Encoding.UTF8.GetBytes("token=" + Uri.EscapeDataString(token) + "&user=" + Uri.EscapeDataString(user) + "&title=" +
                    Uri.EscapeDataString(S(Get(body, "title"))) + "&message=" + Uri.EscapeDataString(S(Get(body, "message"))) + "&priority=0");
                request.ContentLength = data.Length;
                using (Timer deadline = new Timer(delegate { try { request.Abort(); } catch { } }, null, 10000, Timeout.Infinite))
                {
                    using (Stream stream = request.GetRequestStream()) { if (operation.TimedOut) { request.Abort(); return new SendResult { Network = true }; } stream.Write(data, 0, data.Length); }
                    HttpWebResponse response;
                    try { response = (HttpWebResponse)request.GetResponse(); }
                    catch (WebException e) { response = e.Response as HttpWebResponse; if (response == null) return new SendResult { Network = true }; }
                    using (response)
                    using (Stream stream = response.GetResponseStream())
                    using (MemoryStream memory = new MemoryStream())
                    {
                        byte[] chunk = new byte[4096]; int count;
                        while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            if (memory.Length + count > 32768) { request.Abort(); return new SendResult { Http = (int)response.StatusCode, Malformed = true }; }
                            memory.Write(chunk, 0, count);
                        }
                        int? status = null;
                        try { var parsed = Serializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(memory.ToArray())); object value = Get(parsed, "status"); if (value is int || value is long || value is decimal || value is double) { double number = Convert.ToDouble(value, CultureInfo.InvariantCulture); if (number == 1) status = 1; else if (number == 0) status = 0; } }
                        catch { }
                        return new SendResult { Http = (int)response.StatusCode, Status = status };
                    }
                }
            }
            catch { return new SendResult { Network = true }; }
        }
        static string Classify(SendResult r)
        {
            if (r.Http == 200 && r.Status == 1) return "accepted";
            if (r.Http >= 400 && r.Http < 500) return "blocked";
            if (r.Network || r.Http >= 500 || r.Malformed) return "retry";
            return "blocked";
        }
        static string QuarantineMarker(string file) { return Path.Combine(PathAt("quarantine"), Path.GetFileName(file) + ".marker.json"); }
        static bool IsQuarantined(string file) { return File.Exists(QuarantineMarker(file)); }
        static void Quarantine(string file, string stateFile, string reason)
        {
            // Keep original files in place to retain the immutable dedup key. Never reset a
            // corrupted state into pending: the previous remote request may have succeeded.
            string prefix = Path.Combine(PathAt("quarantine"), Path.GetFileName(file));
            foreach (string original in new [] { file, stateFile })
            {
                if (!File.Exists(original)) continue;
                string copy = prefix + (original == file ? ".event.bin" : ".state.bin");
                if (!File.Exists(copy))
                    using (FileStream input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (FileStream output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] chunk = new byte[8192]; int count; long remaining = 2097152;
                        while (remaining > 0 && (count = input.Read(chunk, 0, (int)Math.Min(remaining, chunk.Length))) > 0)
                        { output.Write(chunk, 0, count); remaining -= count; }
                        output.Flush(true);
                    }
            }
            var marker = Obj(); marker["created_at"] = Stamp(); marker["reason"] = reason;
            marker["note"] = "原记录保留，跳过该记录；需人工核查，避免把未知状态当作未发送。";
            WriteObject(QuarantineMarker(file), marker); Log("record_quarantined");
        }
        static bool ValidRecord(Dictionary<string, object> record, string filename)
        {
            string id = S(Get(record, "id")), session = S(Get(record, "session_id")), turn = S(Get(record, "turn_id"));
            if (!Regex.IsMatch(id, "^[a-f0-9]{64}$") || filename != id + ".json") return false;
            long format = N(Get(record, "format_version"));
            if (format < 0 || format > 3) return false;
            if ((format == 0 || format == 1) && (!(Get(record, "title") is string) || !(Get(record, "message") is string))) return false;
            if (S(Get(record, "notification_kind")) == "input_request")
                return session != "" && turn != "" && S(Get(record, "tool_use_id")) != "" && InputEventId(session, turn, S(record["tool_use_id"])) == id;
            if (session != "" && turn != "") return EventId(session, turn) == id;
            // The original direct-send script created frozen format-0/1 test records
            // before official hook identifiers existed. Keep their original immutable ID.
            long version = N(Get(record, "format_version"));
            return (version == 0 || version == 1) && Get(record, "title") is string && Get(record, "message") is string;
        }
        static bool ValidState(Dictionary<string, object> state)
        {
            string outcome = S(Get(state, "outcome"));
            if (outcome == "accepted" || outcome == "suppressed") return true; // Preserve terminal history.
            if (outcome != "" && outcome != "blocked" && outcome != "retry") return false;
            object frozen = Get(state, "notification");
            if (frozen == null) return true;
            var body = frozen as Dictionary<string, object>;
            return body != null && Get(body, "title") is string && Get(body, "message") is string && CharacterCount(S(body["message"])) <= 1024;
        }
        static bool ReadQueued(string file, string stateFile, out Dictionary<string, object> record, out Dictionary<string, object> state)
        {
            record = null; state = null;
            if (IsQuarantined(file)) return false;
            try
            {
                record = ReadObject(file); state = ReadObject(stateFile);
                if (!ValidRecord(record, Path.GetFileName(file)) || !ValidState(state))
                { Quarantine(file, stateFile, "invalid_record_or_state_schema"); return false; }
                return true;
            }
            catch (ArgumentException) { Quarantine(file, stateFile, "invalid_json"); return false; }
            catch (InvalidOperationException) { Quarantine(file, stateFile, "invalid_json"); return false; }
        }
        public static string Cycle()
        {
            lock (cycleLock)
            {
                if (pause || Exists("MAINTENANCE") || Exists("APP_MIGRATING")) return "maintenance";
                busy = true;
                try { return CycleInternal(); }
                finally { busy = false; }
            }
        }
        static string CycleInternal()
        {
            if (Exists("DISABLED")) return "disabled";
            var creds = Credentials(); if (creds == null) return "credentials_missing_or_invalid";
            long now = Now(); string fingerprint = S(creds["fingerprint"]);
            var control = ReadObject(PathAt("control.json"));
            if (S(Get(control, "fingerprint")) != fingerprint)
            {
                control = Obj(); control["fingerprint"] = fingerprint; control["next_at"] = 0; control["failures"] = 0; control["blocked"] = false;
                WriteObject(PathAt("control.json"), control);
            }
            if (B(Get(control, "blocked"))) return "blocked";
            if (now < N(Get(control, "next_at"))) return "waiting";
            string[] files = Directory.GetFiles(PathAt("events"), "*.json"); Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                string stateFile = Path.Combine(PathAt("state"), Path.GetFileName(file)); Dictionary<string, object> record, state;
                if (!ReadQueued(file, stateFile, out record, out state)) continue;
                if (S(Get(state, "outcome")) == "accepted" || S(Get(state, "outcome")) == "suppressed") continue;
                if (!CategoryEnabled(record)) { state["outcome"] = "suppressed"; WriteObject(stateFile, state); Log("notification_category_suppressed"); continue; }
                if (S(Get(state, "outcome")) == "blocked" && S(Get(state, "fingerprint")) == fingerprint) continue;
                var notification = Get(state, "notification") as Dictionary<string, object>;
                if (notification == null)
                {
                    var resolved = Copy(record); int version = (int)N(Get(record, "format_version"));
                    if (version == 3) ApplyPrompt(resolved, S(Get(record, "id")));
                    var metadata = (version == 2 || version == 3) && S(Get(record, "notification_kind")) == "" ? LookupMetadata(record) : Obj();
                    if (version == 3 && S(Get(metadata, "input_session_id")) == S(Get(record, "session_id")) &&
                        S(Get(metadata, "input_turn_id")) == S(Get(record, "turn_id")) && S(Get(metadata, "input_lookup")) == "matched" && Get(metadata, "user_input_preview") is string)
                    {
                        string input = S(metadata["user_input_preview"]); resolved["user_input_captured"] = true;
                        resolved["user_input_preview"] = SliceChars(input, 1025); resolved["user_input_was_cut"] = B(Get(metadata, "user_input_was_cut")) || CharacterCount(input) > 1025;
                        resolved["input_source"] = "exact_turn_transcript";
                    }
                    if (version == 2 || version == 3)
                    {
                        notification = Compose(resolved, metadata, Optional(PathAt("notification-options.json")));
                        notification["input_source"] = S(Get(resolved, "input_source")) == "" ? "unavailable" : resolved["input_source"];
                    }
                    else { notification = Obj(); notification["title"] = Get(record, "title"); notification["message"] = Get(record, "message"); }
                    state["notification"] = notification; WriteObject(stateFile, state);
                }
                if (pause || Exists("MAINTENANCE") || Exists("APP_MIGRATING")) return "maintenance";
                if (Exists("DISABLED")) return "disabled";
                if (!CategoryEnabled(record)) { state["outcome"] = "suppressed"; WriteObject(stateFile, state); continue; }
                control["next_at"] = now + 900000; WriteObject(PathAt("control.json"), control);
                SendResult result;
                var fault = Optional(PathAt("diagnostic-network-failure-once.json"));
                if (S(Get(fault, "id")) == S(Get(record, "id")) && S(Get(record, "source")) == "test")
                { File.Delete(PathAt("diagnostic-network-failure-once.json")); result = new SendResult { Network = true }; Log("diagnostic_network_failure_injected"); }
                else result = TestTransport == null ? Post(notification, S(creds["token"]), S(creds["user"])) : TestTransport(notification, S(creds["token"]), S(creds["user"]));
                string outcome = Classify(result); var next = Obj(); next["outcome"] = outcome; next["attempts"] = N(Get(state, "attempts")) + 1;
                next["fingerprint"] = fingerprint; next["last_attempt_at"] = At(now); next["http"] = result.Http == 0 ? null : (object)result.Http; next["notification"] = notification;
                if (outcome == "accepted")
                {
                    next["accepted_at"] = Stamp(); next["note"] = "Pushover已接受并入队"; control["failures"] = 0; control["next_at"] = Now();
                }
                else if (outcome == "blocked") control["blocked"] = true;
                else
                {
                    long failures = N(Get(control, "failures")) + 1; control["failures"] = failures;
                    control["next_at"] = Now() + (failures == 1 ? 5000 : failures == 2 ? 30000 : 900000);
                }
                WriteObject(stateFile, next); WriteObject(PathAt("control.json"), control); Log(outcome); return outcome;
            }
            return "idle";
        }
        public static Dictionary<string, object> Status()
        {
            var result = Obj(); foreach (string k in new [] { "total", "hook", "test", "recovered", "accepted", "blocked", "pending", "quarantined", "suppressed" }) result[k] = 0L;
            foreach (string file in Directory.GetFiles(PathAt("events"), "*.json"))
            {
                result["total"] = N(result["total"]) + 1;
                if (IsQuarantined(file)) { result["quarantined"] = N(result["quarantined"]) + 1; result["blocked"] = N(result["blocked"]) + 1; result["recovered"] = N(result["recovered"]) + 1; continue; }
                Dictionary<string, object> r, s;
                try { r = ReadObject(file); s = ReadObject(Path.Combine(PathAt("state"), Path.GetFileName(file))); }
                catch (ArgumentException) { result["quarantined"] = N(result["quarantined"]) + 1; result["blocked"] = N(result["blocked"]) + 1; result["recovered"] = N(result["recovered"]) + 1; continue; }
                catch (InvalidOperationException) { result["quarantined"] = N(result["quarantined"]) + 1; result["blocked"] = N(result["blocked"]) + 1; result["recovered"] = N(result["recovered"]) + 1; continue; }
                string source = S(Get(r, "source")); string category = source == "hook" ? "hook" : source == "recovered" ? "recovered" : "test";
                result[category] = N(result[category]) + 1;
                string outcome = S(Get(s, "outcome")); category = outcome == "suppressed" ? "suppressed" : outcome == "accepted" ? "accepted" : outcome == "blocked" ? "blocked" : "pending";
                result[category] = N(result[category]) + 1;
            }
            var control = ReadObject(PathAt("control.json"));
            result["enabled"] = !Exists("DISABLED"); result["credentials_ready"] = Credentials() != null; result["sender_blocked"] = B(Get(control, "blocked"));
            result[SuggestionOption] = OptionEnabled(SuggestionOption); result[InputOption] = OptionEnabled(InputOption);
            result["next_attempt_at"] = N(Get(control, "next_at")) > 0 ? At(N(control["next_at"])) : null; result["busy"] = busy; result["maintenance"] = pause || Exists("MAINTENANCE") || Exists("APP_MIGRATING");
            return result;
        }
        public static void SetEnabled(bool enabled)
        {
            if (enabled) { File.Delete(PathAt("DISABLED")); Log("enabled_by_command"); }
            else { File.WriteAllText(PathAt("DISABLED"), "Notifications disabled.\n", new UTF8Encoding(false)); Log("disabled_by_command"); }
        }
        public static void Resume()
        {
            lock (cycleLock)
            {
                var control = ReadObject(PathAt("control.json"));
                if (B(Get(control, "blocked"))) foreach (string file in Directory.GetFiles(PathAt("state"), "*.json"))
                {
                    string eventFile = Path.Combine(PathAt("events"), Path.GetFileName(file)); if (IsQuarantined(eventFile)) continue;
                    Dictionary<string, object> state;
                    try { state = ReadObject(file); } catch (ArgumentException) { continue; } catch (InvalidOperationException) { continue; }
                    if (S(Get(state, "outcome")) == "blocked") { state["outcome"] = "retry"; WriteObject(file, state); }
                }
                control["blocked"] = false; control["failures"] = 0; control["next_at"] = 0; WriteObject(PathAt("control.json"), control); Log("resumed_by_command");
            }
        }
    }
}
