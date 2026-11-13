using System.Threading;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Browsingway;

// Talks to the Venuekit server (POST /api { fn, args }). Polls every 20 s for in-game notifications.
internal sealed class VenuekitApi : IDisposable
{
	public sealed class Note
	{
		[JsonPropertyName("n")] public long N { get; set; }
		[JsonPropertyName("t")] public string T { get; set; } = "";
		[JsonPropertyName("id")] public string Id { get; set; } = "";
		[JsonPropertyName("from")] public string From { get; set; } = "";
	}

	private sealed class Envelope
	{
		[JsonPropertyName("ok")] public bool Ok { get; set; }
		[JsonPropertyName("error")] public string? Error { get; set; }
		[JsonPropertyName("result")] public JsonElement Result { get; set; }
	}

	private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
	private readonly CancellationTokenSource _stop = new();
	private readonly string _base;
	private string _key;
	private string _ch = "", _world = "";
	private long _since = -1;   // -1: first poll – only remember the newest note, do not show old ones

	public sealed class SdRequest
	{
		[JsonPropertyName("id")] public string Id { get; set; } = "";
		[JsonPropertyName("max")] public int Max { get; set; }
		[JsonPropertyName("round")] public int Round { get; set; }
	}

	public bool Linked { get; private set; }
	public bool Fast { get; private set; }
	// window open → poll every second, so "Roll in-game" starts /random right away
	private volatile bool _active;
	private readonly SemaphoreSlim _wake = new(0);
	public bool Active { get => _active; set { bool was = _active; _active = value; if (value && !was) Wake(); } }
	public void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }
	public event Action<Note>? Notification;
	public event Action<SdRequest>? Roll;
	public event Action<string>? Error;

	public VenuekitApi(string baseUrl, string key)
	{
		_base = baseUrl.TrimEnd('/');
		_key = key;
		_http.DefaultRequestHeaders.UserAgent.ParseAdd("VenuekitPlugin/0.1");
	}

	public void SetKey(string key) { _key = key; Linked = false; _since = -1; }
	public void SetCharacter(string ch, string world) { _ch = ch; _world = world; }

	public void Start() => _ = Task.Run(Loop);

	// Polling only when it is needed (fewer requests = lower server costs):
	//   Showdown match running → 1.5 s · window open → 10 s · just sent a challenge → 10 s for 10 min
	//   window closed, logged in → every 2 min (in-game challenges still arrive) · logged out / not connected → no requests
	private long _inviteUntil;
	public void InviteSent() { _inviteUntil = Environment.TickCount64 + 600000; Wake(); }
	private async Task Loop()
	{
		while (!_stop.IsCancellationRequested)
		{
			bool invite = Environment.TickCount64 < _inviteUntil;
			// window closed: only while logged in with a character and connected (the first poll checks the connection)
			bool due = Fast || _active || (_ch != "" && (Linked || _since < 0));
			if (due)
			{
				try { await Poll(); }
				catch (Exception e) { Error?.Invoke(e.Message); }
			}
			double wait = Fast ? 1.5 : _active ? (Linked ? 10 : 4) : invite ? 10 : due ? 120 : 30;
			try { await _wake.WaitAsync(TimeSpan.FromSeconds(wait), _stop.Token); } catch (OperationCanceledException) { return; }
		}
	}

	private async Task Poll()
	{
		if (_ch == "") return;   // not logged in to a character (title screen)
		JsonElement r = await Call("plugPoll", _key, _ch, _world, Math.Max(0, _since));
		Linked = r.TryGetProperty("linked", out var l) && l.GetBoolean();
		Fast = r.TryGetProperty("fast", out var f) && f.ValueKind == JsonValueKind.True;
		if (r.TryGetProperty("sd", out var sd) && sd.ValueKind == JsonValueKind.Object)
		{
			var q = sd.Deserialize<SdRequest>();
			if (q != null && q.Max > 0) Roll?.Invoke(q);
		}
		if (!Linked || !r.TryGetProperty("notes", out var notes)) return;
		var list = notes.Deserialize<List<Note>>() ?? new();
		bool first = _since < 0;
		foreach (Note n in list.OrderBy(x => x.N))
		{
			if (n.N > _since) _since = n.N;
			if (!first) Notification?.Invoke(n);
		}
		if (_since < 0) _since = 0;
	}

	// rolls seen in the chat (own and others) – Venuekit matches them to running Showdowns
	public async Task ReportRolls(List<(string who, int roll, int max)> rolls)
	{
		try { await Call("plugSdRoll", _key, _ch, _world, rolls.Select(x => new { who = x.who, roll = x.roll, max = x.max }).ToArray()); }
		catch (Exception e) { Error?.Invoke(e.Message); }
	}

	public async Task<(bool ok, string id, string error)> Invite(string toName, string toWorld, bool showdown = false)
	{
		try
		{
			JsonElement r = showdown ? await Call("plugSdInvite", _key, _ch, _world, toName, toWorld) : await Call("plugInvite", _key, _ch, _world, toName, toWorld, "random");
			return (true, r.GetProperty("id").GetString() ?? "", "");
		}
		catch (Exception e) { return (false, "", e.Message); }
	}

	private async Task<JsonElement> Call(string fn, params object[] args)
	{
		using HttpResponseMessage res = await _http.PostAsJsonAsync(_base + "/api", new { fn, args }, _stop.Token);
		Envelope? env = await res.Content.ReadFromJsonAsync<Envelope>(cancellationToken: _stop.Token);
		if (env == null) throw new Exception("No answer from Venuekit.");
		if (!env.Ok) throw new Exception(System.Text.RegularExpressions.Regex.Replace(env.Error ?? "Something went wrong.", @"^\[\w+\]\s*", ""));
		return env.Result;
	}

	public void Dispose()
	{
		_stop.Cancel();
		_http.Dispose();
	}
}
