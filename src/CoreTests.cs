using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using CodexPushover;

public static class CoreTests
{
    static string directory;
    static int passed;
    static long now;
    static JavaScriptSerializer js = new JavaScriptSerializer();
    static Dictionary<string, object> O() { return new Dictionary<string, object>(); }
    static string F(string name) { return Path.Combine(directory, name); }
    static void Require(bool condition, string assertion) { if (!condition) throw new Exception(assertion); }
    static void RemoveTestDirectory()
    {
        if (directory == null || !Directory.Exists(directory)) return;
        string path = Path.GetFullPath(directory), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!String.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "^CodexPushoverCoreTests-[a-f0-9]{32}$"))
            throw new InvalidOperationException("Refused cleanup outside the isolated test directory.");
        Directory.Delete(path, true);
    }
    static void Fresh()
    {
        RemoveTestDirectory();
        directory = Path.Combine(Path.GetTempPath(), "CodexPushoverCoreTests-" + Guid.NewGuid().ToString("N"));
        Core.Initialize(directory); Core.Pause = false; Core.TestClock = delegate { return now; }; now = 1000000000000;
        Core.TestMetadata = delegate { return O(); };
        Core.TestTransport = delegate { return new SendResult { Http = 200, Status = 1 }; };
        var c = O(); c["PUSHOVER_APP_TOKEN"] = new String('A', 30); c["PUSHOVER_USER_KEY"] = new String('B', 30); Core.WriteObject(F("credentials.json"), c);
        var options = O(); options["content_mode"] = "user_input"; options["user_input_max_chars"] = 200; Core.WriteObject(F("notification-options.json"), options);
    }
    static Dictionary<string, object> Event(string kind, string turn, string text)
    {
        var e = O(); e["hook_event_name"] = kind; e["session_id"] = "session-中文😀"; e["turn_id"] = turn; e["cwd"] = "C:\\projects\\项目";
        e[kind == "Stop" ? "last_assistant_message" : "prompt"] = text; return e;
    }
    static string Hook(string kind, string turn, string text) { return (string)Core.HandleHook(js.Serialize(Event(kind, turn, text)))["outcome"]; }
    static Dictionary<string, object> State(string turn) { return Core.ReadObject(F("state\\" + Core.EventId("session-中文😀", turn) + ".json")); }
    static Dictionary<string, object> Notification(string turn) { return (Dictionary<string, object>)State(turn)["notification"]; }
    const string SuggestionPrompt = "This is an app-generated request for a suggested next user message. The user did not write this message.\n\nYour suggestion will appear in the user's empty message composer.";
    static string Ask(string turn, string call, bool asyncTool)
    {
        var e = Event("PreToolUse", turn, ""); e.Remove("prompt");
        e["tool_name"] = asyncTool ? "functions.request_user_input_async" : "request_user_input"; e["tool_use_id"] = call;
        var q = O(); q[asyncTool ? "title" : "question"] = "请选择处理方式？";
        q["options"] = asyncTool ? new object[] { "继续", "稍后" } : new object[] { new Dictionary<string, object> { {"label", "继续"}, {"description", "开始处理"} }, new Dictionary<string, object> { {"label", "稍后"} } };
        e["tool_input"] = new Dictionary<string, object> { {"questions", new object[] { q }} };
        return (string)Core.HandleHook(js.Serialize(e))["outcome"];
    }
    static void Test(string name, Action action) { Fresh(); action(); passed++; Console.WriteLine("PASS " + name); }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "metadata-check")
            {
                Fresh(); Core.TestMetadata = null; Core.TestDiagnostic = delegate(string v) { Console.WriteLine("metadata_check_diagnostic: " + v); };
                Core.WriteObject(F("installation.json"), Core.ReadObject(args[1]));
                var record = Core.ReadObject(args[2]); var metadata = Core.LookupMetadata(record);
                var brief = O(); brief["metadata_source"] = metadata.ContainsKey("metadata_source") ? metadata["metadata_source"] : null;
                brief["project_resolved"] = metadata.ContainsKey("project_name"); brief["input_lookup"] = metadata.ContainsKey("input_lookup") ? metadata["input_lookup"] : null;
                brief["exact_session"] = metadata.ContainsKey("input_session_id") && (string)metadata["input_session_id"] == (string)record["session_id"];
                brief["exact_turn"] = metadata.ContainsKey("input_turn_id") && (string)metadata["input_turn_id"] == (string)record["turn_id"];
                Console.WriteLine(js.Serialize(brief)); return (string)brief["metadata_source"] == "live_engine_readonly_sqlite" ? 0 : 1;
            }
            Test("composer suggestion requests never enqueue or call transport", delegate {
                string prompt = "This is an app-generated request for a suggested next user message. The user did not write this message.\n\nYour suggestion will appear in the user's empty message composer. They can accept it, edit it, or ignore it; it will not be sent automatically.";
                Require(Hook("UserPromptSubmit", "suggestion", prompt) == "ignored_suggestion", "suggestion prompt not filtered");
                var saved = Core.ReadObject(F("prompts\\" + Core.EventId("session-中文😀", "suggestion") + ".json"));
                Require(saved.ContainsKey("composer_suggestion") && !saved.ContainsKey("user_input_preview"), "background context retained");
                Core.Initialize(directory); // Stop may run in a different EXE process.
                Require(Hook("Stop", "suggestion", "{\"suggestion\":\"继续检查\"}") == "ignored_suggestion", "suggestion Stop not filtered");
                Require(Hook("Stop", "suggestion", "{\"suggestion\":\"继续检查\"}") == "ignored_suggestion", "repeated suggestion not filtered");
                Core.TestTransport = delegate { throw new Exception("suggestion reached network transport"); };
                Require(Core.Cycle() == "idle" && Directory.GetFiles(F("events"), "*.json").Length == 0, "suggestion enqueued");
            });
            Test("normal reply and next normal turn survive suggestion in same session", delegate {
                string prefix = "This is an app-generated request for a suggested next user message. The user did not write this message.\n\nYour suggestion will appear in the user's empty message composer.";
                Hook("UserPromptSubmit", "suggestion", prefix); Hook("Stop", "suggestion", "{\"suggestion\":\"继续\"}");
                Hook("UserPromptSubmit", "normal", "请回复结果"); Require(Hook("Stop", "normal", "正常回复") == "queued", "normal turn filtered");
                Require(Core.Cycle() == "accepted", "normal reply not sent");
                Hook("UserPromptSubmit", "next", "下一轮"); Hook("Stop", "next", "下一轮回复");
                Require(Core.Cycle() == "accepted" && Convert.ToInt64(Core.Status()["accepted"]) == 2, "next turn swallowed");
            });
            Test("user-requested suggestion JSON and literal suggestion discussion remain eligible", delegate {
                Hook("UserPromptSubmit", "json", "请返回 JSON，唯一字段为 suggestion");
                Require(Hook("Stop", "json", "{\"suggestion\":\"用户主动请求的建议\"}") == "queued" && Core.Cycle() == "accepted", "legitimate JSON filtered");
                Hook("UserPromptSubmit", "discussion", "解释这句模板：This is an app-generated request for a suggested next user message. The user did not write this message. Your suggestion will appear in the user's empty message composer.");
                Require(Hook("Stop", "discussion", "模板解释") == "queued" && Core.Cycle() == "accepted", "ordinary discussion filtered");
            });
            Test("pre-upgrade cached suggestion prompt is recognized at Stop", delegate {
                var saved = O(); saved["session_id"] = "session-中文😀"; saved["turn_id"] = "legacy-suggestion";
                saved["user_input_preview"] = "This is an app-generated request for a suggested next user message. The user did not write this message.\n\nYour suggestion will appear in the user's empty message composer.";
                Core.WriteObject(F("prompts\\" + Core.EventId("session-中文😀", "legacy-suggestion") + ".json"), saved);
                Require(Hook("Stop", "legacy-suggestion", "{\"suggestion\":\"旧回合建议\"}") == "ignored_suggestion", "legacy cached suggestion queued");
                Require(Core.Cycle() == "idle", "legacy suggestion sent");
            });
            Test("suggestion opt-in persists and sends only proposed text", delegate {
                Core.SetOption(Core.SuggestionOption, true); Core.Initialize(directory);
                Require(Core.OptionEnabled(Core.SuggestionOption) && !Core.OptionEnabled(Core.InputOption), "independent persisted option");
                Require(Hook("UserPromptSubmit", "opt-in", SuggestionPrompt + "\nPRIVATE_CONTEXT") == "captured_suggestion", "marker");
                Require(Hook("Stop", "opt-in", "{\"suggestion\":\"继续检查结果\"}") == "queued" && Core.Cycle() == "accepted", "suggestion delivery");
                var n = Notification("opt-in"); string body = (string)n["message"];
                Require((string)n["title"] == "Codex 有新的输入建议" && body.Contains("继续检查结果") && !body.Contains("PRIVATE_CONTEXT") && !body.Contains("用户输入"), "unsafe suggestion body");
                Require(!File.ReadAllText(F("prompts\\" + Core.EventId("session-中文😀", "opt-in") + ".json")).Contains("PRIVATE_CONTEXT"), "context persisted");
                Require(Hook("Stop", "opt-in", "{\"suggestion\":\"changed\"}") == "duplicate" && Core.Cycle() == "idle", "duplicate suggestion");
            });
            Test("empty malformed and null suggestions are silent", delegate {
                Core.SetOption(Core.SuggestionOption, true); int calls = 0; Core.TestTransport = delegate { calls++; return new SendResult(); };
                string[] replies = { "{\"suggestion\":null}", "{\"suggestion\":\"  \"}", "malformed", "{\"unexpected\":\"context\"}" };
                for (int i = 0; i < replies.Length; i++) { string turn = "empty" + i; Hook("UserPromptSubmit", turn, SuggestionPrompt); Require(Hook("Stop", turn, replies[i]) == "empty_suggestion", "empty suggestion queued"); }
                Require(Core.Cycle() == "idle" && calls == 0, "empty called API");
            });
            Test("category off cancels pending retries without affecting replies", delegate {
                Core.SetOption(Core.SuggestionOption, true); Hook("UserPromptSubmit", "off", SuggestionPrompt); Hook("Stop", "off", "{\"suggestion\":\"draft\"}");
                Core.TestTransport = delegate { return new SendResult { Network = true }; }; Require(Core.Cycle() == "retry", "retry");
                Core.SetOption(Core.SuggestionOption, false); Require((string)State("off")["outcome"] == "suppressed", "pending not cancelled during backoff");
                Core.SetOption(Core.SuggestionOption, true); now += 5000; int calls = 0;
                Core.TestTransport = delegate { calls++; return new SendResult { Http = 200, Status = 1 }; };
                Hook("Stop", "normal", "正式回复"); Require(Core.Cycle() == "accepted" && Core.Cycle() == "idle" && calls == 1, "cancelled suggestion replayed");
                Require(Convert.ToInt64(Core.Status()["suppressed"]) == 1 && Convert.ToInt64(Core.Status()["pending"]) == 0, "cancel counts");
            });
            Test("input requests default off and require explicit valid identifiers", delegate {
                Require(Ask("ask", "c1", false) == "input_notifications_disabled", "old options enabled input");
                Core.SetOption(Core.InputOption, true);
                var e = Event("PreToolUse", "ask", ""); e["tool_name"] = "request_user_input";
                Require((string)Core.HandleHook(js.Serialize(e))["outcome"] == "missing_tool_use_id", "missing call accepted");
                e["tool_use_id"] = "c1"; Require((string)Core.HandleHook(js.Serialize(e))["outcome"] == "missing_questions", "missing questions accepted");
                e["tool_name"] = "Bash"; Require((string)Core.HandleHook(js.Serialize(e))["outcome"] == "ignored_tool", "other tool captured");
                e["tool_name"] = "request_user_input"; e["agent_id"] = "child"; Require((string)Core.HandleHook(js.Serialize(e))["outcome"] == "ignored_subagent", "subagent captured");
            });
            Test("same-turn questions and Stop have separate stable dedup keys", delegate {
                Core.SetOption(Core.InputOption, true);
                Require(Ask("one", "call-a", false) == "queued" && Ask("one", "call-a", false) == "duplicate", "question dedup");
                Require(Ask("one", "call-b", true) == "queued" && Hook("Stop", "one", "正式回复") == "queued", "question or Stop swallowed");
                int calls = 0, questions = 0; Core.TestTransport = delegate(Dictionary<string, object> n, string t, string u) {
                    calls++; if ((string)n["title"] == "Codex 需要你回答") { questions++; Require(((string)n["message"]).Contains("请选择处理方式？") && ((string)n["message"]).Contains("• 继续") && ((string)n["message"]).Contains("• 稍后"), "question/options missing"); }
                    return new SendResult { Http = 200, Status = 1 };
                };
                for (int i = 0; i < 3; i++) Require(Core.Cycle() == "accepted", "missing notification");
                Require(calls == 3 && questions == 2 && Core.Cycle() == "idle", "wrong counts");
                Ask("two", "call-a", true); Require(Core.Cycle() == "accepted", "next turn swallowed");
            });
            Test("concurrent duplicate input requests publish once", delegate {
                Core.SetOption(Core.InputOption, true); int queued = 0; Exception error = null;
                var threads = new Thread[16]; for (int i = 0; i < threads.Length; i++) { threads[i] = new Thread(delegate() { try { if (Ask("ask", "same-call", true) == "queued") Interlocked.Increment(ref queued); } catch (Exception e) { error = e; } }); threads[i].Start(); }
                foreach (var thread in threads) thread.Join(); Require(error == null && queued == 1 && Core.Cycle() == "accepted", "concurrent input dedup");
            });
            Test("input opt-out leaves suggestion opt-in and accepted history intact", delegate {
                Core.SetOption(Core.InputOption, true); Core.SetOption(Core.SuggestionOption, true);
                Ask("a", "accepted", true); Core.Cycle(); string path = F("state\\" + Core.InputEventId("session-中文😀", "a", "accepted") + ".json"), accepted = File.ReadAllText(path);
                Ask("b", "pending", true); Core.SetOption(Core.InputOption, false); Core.Initialize(directory);
                Require(File.ReadAllText(path) == accepted && Core.OptionEnabled(Core.SuggestionOption) && !Core.OptionEnabled(Core.InputOption), "accepted/options changed");
                Core.SetOption(Core.InputOption, true); Require(Core.Cycle() == "idle", "cancelled question replayed");
                Core.SetEnabled(false); Require(Ask("c", "disabled", true) == "disabled", "master switch bypassed");
            });
            Test("interaction Unicode body fits API limit and marks truncation", delegate {
                Core.SetOption(Core.SuggestionOption, true); Hook("UserPromptSubmit", "long", SuggestionPrompt);
                Hook("Stop", "long", js.Serialize(new Dictionary<string, object> { {"suggestion", new String('问', 2000) + "😀"} })); Core.Cycle();
                string body = (string)Notification("long")["message"]; Require(Core.CharacterCount(body) <= 1024 && body.Contains("已截断"), "long suggestion");
            });
            Test("session-turn keys retain existing Node JSON SHA256 compatibility", delegate {
                Require(Core.EventId("session-中文😀", "turn-1") == "e027ab7670a603ccdb40c99ec2640793daa4f03246bfa9b00d034b431815027b", "Unicode key mismatch");
                Require(Core.EventId("x\n\"\\", "\ud800") == "804ce3ebea1da9bb4400892e9e290a50ac8871cd56f5b32656f8a5e46eb24582", "Escaped key mismatch");
            });
            Test("concurrent repeated Stop publishes one immutable event", delegate {
                int queued = 0; Exception error = null; Thread[] threads = new Thread[24];
                for (int i = 0; i < threads.Length; i++) { threads[i] = new Thread(delegate() { try { if (Hook("Stop", "one", "first") == "queued") Interlocked.Increment(ref queued); } catch (Exception e) { error = e; } }); threads[i].Start(); }
                foreach (Thread t in threads) t.Join(); Require(error == null && queued == 1, "duplicate publication");
                Require(Directory.GetFiles(F("events"), "*.json").Length == 1, "event count");
                Require(Hook("Stop", "one", "replacement") == "duplicate", "same turn");
                var r = Core.ReadObject(Directory.GetFiles(F("events"), "*.json")[0]); Require((string)r["reply_preview"] == "first", "immutable event replaced");
            });
            Test("next turn in same session remains eligible", delegate {
                Hook("Stop", "one", "first"); Require(Core.Cycle() == "accepted", "first turn");
                Hook("Stop", "two", "second"); Require(Core.Cycle() == "accepted", "second turn"); Require(Convert.ToInt64(Core.Status()["accepted"]) == 2, "next turn swallowed");
            });
            Test("prompt capture uses exact session and turn, latest submission", delegate {
                Hook("UserPromptSubmit", "one", "earlier"); Hook("UserPromptSubmit", "one", "latest"); Hook("UserPromptSubmit", "two", "other");
                Hook("Stop", "one", "answer"); Require(Core.Cycle() == "accepted", "send");
                string body = (string)Notification("one")["message"]; Require(body.Contains("latest") && !body.Contains("earlier") && !body.Contains("other"), "prompt mismatch");
            });
            Test("question reply wrapper only displays answer", delegate {
                Require(Core.VisibleInput("<send_user_message_question_reply>[{\"questionItemId\":\"secret-question\",\"answer\":\"已确认\"}]</send_user_message_question_reply>") == "已确认", "wrapper leaked");
            });
            Test("transcript must match both session and explicit turn", delegate {
                Hook("UserPromptSubmit", "one", "hook exact"); Hook("Stop", "one", "answer");
                Core.TestMetadata = delegate { var m = O(); m["input_session_id"] = "wrong"; m["input_turn_id"] = "one"; m["input_lookup"] = "matched"; m["user_input_preview"] = "wrong transcript"; return m; };
                Core.Cycle(); Require(((string)Notification("one")["message"]).Contains("hook exact"), "cross-session match");
                Hook("UserPromptSubmit", "two", "hook two"); Hook("Stop", "two", "answer");
                Core.TestMetadata = delegate { var m = O(); m["input_session_id"] = "session-中文😀"; m["input_turn_id"] = "two"; m["input_lookup"] = "matched"; m["user_input_preview"] = "transcript exact"; return m; };
                Core.Cycle(); Require(((string)Notification("two")["message"]).Contains("transcript exact"), "precise transcript fallback");
            });
            Test("Unicode body fits 1024 codepoints and marks reply truncation", delegate {
                Hook("UserPromptSubmit", "one", new String('问', 600));
                Hook("Stop", "one", String.Join("", new String[1]) + new String('答', 2000) + "😀"); Core.Cycle();
                string body = (string)Notification("one")["message"]; Require(Core.CharacterCount(body) <= 1024, "oversized body");
                Require(body.Contains("输入已截断") && body.Contains("回复已截断"), "missing markers");
                Require(Core.CharacterCount("😀😀中文") == 4, "surrogate count");
            });
            Test("retry body remains frozen after metadata and prompt changes", delegate {
                Hook("UserPromptSubmit", "one", "original input"); Hook("Stop", "one", "original reply"); string first = null;
                Core.TestTransport = delegate(Dictionary<string, object> b, string token, string user) { first = (string)b["message"]; return new SendResult { Network = true }; };
                Require(Core.Cycle() == "retry", "retry result"); Hook("UserPromptSubmit", "one", "changed input");
                Core.TestMetadata = delegate { var m = O(); m["project_name"] = "renamed"; return m; };
                now += 5000; Core.TestTransport = delegate(Dictionary<string, object> b, string token, string user) { Require((string)b["message"] == first, "body changed"); return new SendResult { Http = 200, Status = 1 }; };
                Require(Core.Cycle() == "accepted", "recovery");
            });
            Test("network retries observe 5s 30s and durable 15min intervals", delegate {
                Hook("Stop", "one", "answer"); int calls = 0; Core.TestTransport = delegate { calls++; return new SendResult { Network = true }; };
                Require(Core.Cycle() == "retry" && Core.Cycle() == "waiting", "immediate retry");
                Require(Convert.ToInt64(Core.ReadObject(F("control.json"))["next_at"]) == now + 5000, "5s spacing");
                now += 5000; Core.Cycle(); Require(Convert.ToInt64(Core.ReadObject(F("control.json"))["next_at"]) == now + 30000, "30s spacing");
                now += 30000; Core.Cycle(); Require(Convert.ToInt64(Core.ReadObject(F("control.json"))["next_at"]) == now + 900000 && calls == 3, "circuit spacing");
                Core.Initialize(directory); Require(Core.Cycle() == "waiting", "restart discarded cooldown");
                now += 900000; Core.TestTransport = delegate { return new SendResult { Http = 200, Status = 1 }; }; Require(Core.Cycle() == "accepted", "offline backlog not recovered");
            });
            Test("4xx stops retry until explicit resume", delegate {
                Hook("Stop", "one", "answer"); int calls = 0; Core.TestTransport = delegate { calls++; return new SendResult { Http = 400, Status = 0 }; };
                Require(Core.Cycle() == "blocked", "4xx classification"); now += 99999999;
                Require(Core.Cycle() == "blocked" && calls == 1, "blind retry"); Core.Resume();
                Core.TestTransport = delegate { return new SendResult { Http = 200, Status = 1 }; }; Require(Core.Cycle() == "accepted", "resume eligibility");
            });
            Test("HTTP 200 status zero is not accepted", delegate {
                Hook("Stop", "one", "answer"); Core.TestTransport = delegate { return new SendResult { Http = 200, Status = 0 }; };
                Require(Core.Cycle() == "blocked" && Convert.ToInt64(Core.Status()["accepted"]) == 0, "false acceptance");
            });
            Test("HTTP 5xx is retained for retry", delegate {
                Hook("Stop", "one", "answer"); Core.TestTransport = delegate { return new SendResult { Http = 503 }; };
                Require(Core.Cycle() == "retry" && Convert.ToInt64(Core.Status()["pending"]) == 1, "5xx handling");
            });
            Test("maintenance retains queue and makes no network calls", delegate {
                Hook("Stop", "one", "answer"); int calls = 0; Core.TestTransport = delegate { calls++; return new SendResult { Http = 200, Status = 1 }; };
                Core.Pause = true; Require(Core.Cycle() == "maintenance" && calls == 0, "Pause ignored"); Core.Pause = false;
                File.WriteAllText(F("MAINTENANCE"), ""); Require(Core.Cycle() == "maintenance" && calls == 0, "marker ignored");
                File.Delete(F("MAINTENANCE")); Require(Core.Cycle() == "accepted" && calls == 1, "queue discarded");
            });
            Test("disable remains closed and enable retains backlog", delegate {
                Hook("Stop", "one", "answer"); Core.SetEnabled(false); Require(Hook("Stop", "two", "answer") == "disabled" && Core.Cycle() == "disabled", "disabled ignored");
                Core.SetEnabled(true); Require(Core.Cycle() == "accepted" && Convert.ToInt64(Core.Status()["total"]) == 1, "enable altered queue");
            });
            Test("subagent and missing identifiers are rejected", delegate {
                var e = Event("Stop", "one", "answer"); e["agent_id"] = "child"; Require((string)Core.HandleHook(js.Serialize(e))["outcome"] == "ignored_subagent", "subagent sent");
                e.Remove("agent_id"); e.Remove("turn_id"); Core.HandleHook(js.Serialize(e)); Require(Convert.ToInt64(Core.Status()["total"]) == 0, "no turn accepted");
            });
            Test("legacy accepted state is preserved across migration", delegate {
                Hook("Stop", "one", "answer"); string file = F("state\\" + Core.EventId("session-中文😀", "one") + ".json");
                var s = O(); s["outcome"] = "accepted"; s["attempts"] = 3; s["note"] = "Pushover已接受并入队"; Core.WriteObject(file, s);
                int calls = 0; Core.TestTransport = delegate { calls++; return new SendResult { Http = 200, Status = 1 }; };
                Require(Core.Cycle() == "idle" && calls == 0 && Convert.ToInt64(Core.ReadObject(file)["attempts"]) == 3, "accepted migration resend");
            });
            Test("legacy format two title notification remains sendable", delegate {
                var options = O(); Core.WriteObject(F("notification-options.json"), options); Hook("Stop", "one", "answer");
                Core.TestMetadata = delegate { var m = O(); m["conversation_title"] = "旧对话"; return m; };
                Core.Cycle(); Require(((string)Notification("one")["message"]).Contains("对话标题：旧对话"), "legacy format lost");
            });
            Test("invalid credentials never call transport", delegate {
                File.Delete(F("credentials.json")); Hook("Stop", "one", "answer"); int calls = 0; Core.TestTransport = delegate { calls++; return new SendResult(); };
                Require(Core.Cycle() == "credentials_missing_or_invalid" && calls == 0, "missing credential request");
            });
            Test("desktop project fallback honors explicit assignment", delegate {
                Core.TestMetadata = null;
                string config = F("config"); Directory.CreateDirectory(config);
                var installation = O(); installation["config_home"] = config; Core.WriteObject(F("installation.json"), installation);
                var global = O(); var projects = O(); var project = O(); project["name"] = "指定项目"; project["rootPaths"] = new [] { "C:\\unrelated" }; projects["project-id"] = project;
                var assignments = O(); var assignment = O(); assignment["projectId"] = "project-id"; assignments["session-中文😀"] = assignment;
                global["local-projects"] = projects; global["thread-project-assignments"] = assignments; Core.WriteObject(Path.Combine(config, ".codex-global-state.json"), global);
                Hook("Stop", "one", "answer"); Core.Cycle(); Require(((string)Notification("one")["message"]).StartsWith("项目名称：指定项目\n"), "assignment ignored");
            });
            Test("migration marker prevents all starting network requests", delegate {
                Hook("Stop", "one", "answer"); int calls = 0; Core.TestTransport = delegate { calls++; return new SendResult { Http = 200, Status = 1 }; };
                File.WriteAllText(F("APP_MIGRATING"), "review required"); Require(Core.Cycle() == "maintenance" && calls == 0, "migration network started");
                File.Delete(F("APP_MIGRATING")); Require(Core.Cycle() == "accepted", "migration queue lost");
            });
            Test("corrupted state is quarantined without resending and healthy next turn proceeds", delegate {
                Hook("Stop", "bad", "unknown accepted reply"); Hook("Stop", "good", "good reply");
                string state = F("state\\" + Core.EventId("session-中文😀", "bad") + ".json"); File.WriteAllText(state, "{\"outcome\":\"accepted\",broken");
                int calls = 0; Core.TestTransport = delegate(Dictionary<string, object> b, string t, string u) { Require(!((string)b["message"]).Contains("unknown accepted reply"), "corrupt accepted resend"); calls++; return new SendResult { Http = 200, Status = 1 }; };
                Require(Core.Cycle() == "accepted" && Core.Cycle() == "idle" && calls == 1, "bad record blocked healthy queue");
                Require(Convert.ToInt64(Core.Status()["quarantined"]) == 1 && File.ReadAllText(state).Contains("broken"), "original state lost");
                Core.Resume(); Require(Core.Cycle() == "idle" && calls == 1, "resume resent quarantine");
            });
            Test("broken event JSON is isolated and immutable key remains reserved", delegate {
                Hook("Stop", "bad", "bad reply"); Hook("Stop", "good", "good reply");
                string file = F("events\\" + Core.EventId("session-中文😀", "bad") + ".json"); File.WriteAllText(file, "{broken");
                Require(Core.Cycle() == "accepted" && Core.Cycle() == "idle", "bad event blocked queue");
                Require(Hook("Stop", "bad", "replacement") == "duplicate" && File.ReadAllText(file) == "{broken", "unknown dedup state reset");
                Require(Convert.ToInt64(Core.Status()["quarantined"]) == 1, "no quarantine marker");
            });
            Test("legacy format one frozen message remains byte-for-byte stable", delegate {
                Hook("Stop", "one", "answer"); string file = F("events\\" + Core.EventId("session-中文😀", "one") + ".json");
                var record = Core.ReadObject(file); record["format_version"] = 1; record["title"] = "legacy title"; record["message"] = "legacy exact message"; Core.WriteObject(file, record);
                Core.TestMetadata = delegate { throw new Exception("legacy format unexpectedly queried metadata"); };
                Require(Core.Cycle() == "accepted", "format1 failed"); Require((string)Notification("one")["title"] == "legacy title" && (string)Notification("one")["message"] == "legacy exact message", "format1 modified");
            });
            Test("original pre-hook direct-send accepted records need no session identifiers", delegate {
                string id = new String('a', 64); var record = O(); record["id"] = id; record["source"] = "test"; record["title"] = "original title"; record["message"] = "original frozen body";
                Core.WriteObject(F("events\\" + id + ".json"), record); var state = O(); state["outcome"] = "accepted"; state["attempts"] = 1; Core.WriteObject(F("state\\" + id + ".json"), state);
                Core.TestTransport = delegate { throw new Exception("legacy accepted record resent"); };
                Require(Core.Cycle() == "idle" && Convert.ToInt64(Core.Status()["accepted"]) == 1 && Convert.ToInt64(Core.Status()["quarantined"]) == 0, "legacy record reclassified");
            });
            Test("in-progress attempt cooldown survives interrupted sender recovery", delegate {
                Hook("Stop", "one", "answer"); Core.TestTransport = delegate { throw new Exception("simulate process interruption"); };
                try { Core.Cycle(); } catch { }
                Require(!Core.Busy, "busy remained true"); Core.TestTransport = delegate { throw new Exception("cooldown ignored"); };
                Require(Core.Cycle() == "waiting", "crash cooldown reset"); now += 900000;
                Core.TestTransport = delegate { return new SendResult { Http = 200, Status = 1 }; }; Require(Core.Cycle() == "accepted", "crash record lost");
            });
            Test("malformed frozen payload is quarantined before it can block the service", delegate {
                Hook("Stop", "bad", "bad reply"); Hook("Stop", "good", "good reply");
                var state = O(); state["outcome"] = "retry"; var notification = O(); notification["title"] = "title"; notification["message"] = new object[] { "invalid body" }; state["notification"] = notification;
                Core.WriteObject(F("state\\" + Core.EventId("session-中文😀", "bad") + ".json"), state); int calls = 0;
                Core.TestTransport = delegate { calls++; return new SendResult { Http = 200, Status = 1 }; };
                Require(Core.Cycle() == "accepted" && Core.Cycle() == "idle" && calls == 1 && Convert.ToInt64(Core.Status()["quarantined"]) == 1, "malformed body sent");
            });
            Test("native HTTP transport returns within 10s against local stalled response", delegate {
                // HttpListener URL registration requires elevation on this account. A user-level
                // raw loopback TCP HTTP peer tests the same HttpWebRequest without URL ACL changes.
                var server = new TcpListener(IPAddress.Loopback, 0); server.Start(); int port = ((IPEndPoint)server.LocalEndpoint).Port;
                TcpClient connected = null; var serverThread = new Thread(delegate() { try { connected = server.AcceptTcpClient(); byte[] one = new byte[1]; connected.GetStream().Read(one, 0, 1); Thread.Sleep(12000); } catch { } }); serverThread.IsBackground = true; serverThread.Start();
                try
                {
                    var body = O(); body["title"] = "isolated fake transport test"; body["message"] = "local loopback only";
                    MethodInfo post = typeof(Core).GetMethod("PostEndpoint", BindingFlags.NonPublic | BindingFlags.Static);
                    var clock = Stopwatch.StartNew(); var result = (SendResult)post.Invoke(null, new object[] { body, new String('A', 30), new String('B', 30), "http://127.0.0.1:" + port + "/stall" }); clock.Stop();
                    Require(connected != null, "loopback endpoint was not contacted");
                    Require(result.Network && clock.ElapsedMilliseconds >= 9500 && clock.ElapsedMilliseconds < 11500, "absolute timeout failed");
                    Console.WriteLine("loopback_absolute_timeout_ms=" + clock.ElapsedMilliseconds);
                }
                finally { if (connected != null) connected.Close(); server.Stop(); }
            });
            Console.WriteLine("PASSED " + passed + " isolated tests; no production credentials read or Pushover API requests made."); return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); return 1; }
        finally { RemoveTestDirectory(); }
    }
}
