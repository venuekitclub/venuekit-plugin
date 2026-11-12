using Browsingway.Common;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using System.Text.RegularExpressions;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;

namespace Browsingway;

// Venuekit in-game plugin: Venue Clash (venuekit.club) in an in-game window + in-game duel invites.
// The browser part (render process, shared textures, input) comes from Browsingway by Styr1x (GPL-3.0).
public class Plugin : IDalamudPlugin
{
	private static readonly string[] _commands = { "/clash", "/venuekit" };

	private readonly DependencyManager _dependencyManager;
	private readonly string _pluginConfigDir;
	private readonly string _pluginDir;
	private readonly Configuration _config;
	private readonly VenuekitApi _api;
	private readonly InlayConfiguration _window;

	private RenderProcess? _renderProcess;
	private Overlay? _overlay;
	private Services _services;
	private bool _settingsOpen;
	private string _charName = "", _charWorld = "";
	private long _lastCharCheck;
	private string? _pendingUrl;

	public Plugin(IDalamudPluginInterface pluginInterface)
	{
		_services = pluginInterface.Create<Services>()!;

		_pluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? "";
		if (String.IsNullOrEmpty(_pluginDir)) throw new Exception("Could not determine plugin directory");
		_pluginConfigDir = pluginInterface.GetPluginConfigDirectory();

		_config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
		if (string.IsNullOrEmpty(_config.Key))
		{
			_config.Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
			Save();
		}

		_window = new InlayConfiguration
		{
			Guid = _config.WindowGuid,
			Name = "Venue Clash",
			Url = StartUrl(),
			Hidden = !_config.Visible,
			Locked = _config.Locked,
			Zoom = _config.Zoom,
			Opacity = _config.Opacity,
			Muted = _config.Muted,
			Framerate = 60,
		};

		_api = new VenuekitApi(_config.BaseUrl, _config.Key);
		_api.Notification += OnNote;
		_api.Roll += OnRollRequest;
		_api.Error += msg => Services.PluginLog.Warning("[venuekit] " + msg);

		_dependencyManager = new DependencyManager(_pluginDir, _pluginConfigDir);
		_dependencyManager.DependenciesReady += (_, _) => DependenciesReady();
		_dependencyManager.Initialise();

		pluginInterface.UiBuilder.Draw += Render;
		pluginInterface.UiBuilder.OpenMainUi += Toggle;
		pluginInterface.UiBuilder.OpenConfigUi += () => _settingsOpen = true;

		foreach (string c in _commands)
		{
			Services.CommandManager.AddHandler(c, new CommandInfo(HandleCommand)
			{
				HelpMessage = c == "/clash" ? "Open / close Venue Clash. '/clash duel' challenges your target · '/clash settings' · '/clash help'" : "Same as /clash",
				ShowInHelp = c == "/clash"
			});
		}

		Services.ContextMenu.OnMenuOpened += OnMenuOpened;
		Services.Framework.Update += OnFrameworkUpdate;
		Services.Chat.ChatMessage += OnChat;
		_api.Start();
	}

	public string AssemblyLocation { get; } = Assembly.GetExecutingAssembly().Location;
	public string Name => "Venuekit";

	public void Dispose()
	{
		Services.ContextMenu.OnMenuOpened -= OnMenuOpened;
		Services.Framework.Update -= OnFrameworkUpdate;
		Services.Chat.ChatMessage -= OnChat;
		foreach (string c in _commands) Services.CommandManager.RemoveHandler(c);
		_api.Dispose();
		_overlay?.Dispose();
		_renderProcess?.Dispose();
		WndProcHandler.Shutdown();
		DxHandler.Shutdown();
		_dependencyManager.Dispose();
	}

	private void Save() => Services.PluginInterface.SavePluginConfig(_config);

	private string StartUrl(string extra = "")
	{
		string url = _config.BaseUrl.TrimEnd('/') + "/cards?overlay=1&plug=" + _config.Key;
		if (_charName != "" && _charWorld != "")
			url += "&ch=" + Uri.EscapeDataString(_charName) + "&w=" + Uri.EscapeDataString(_charWorld);
		return url + extra;
	}

	// ---------------- browser window (from Browsingway) ----------------
	private void DependenciesReady()
	{
		DxHandler.Initialise(Services.PluginInterface);
		WndProcHandler.Initialise(DxHandler.WindowHandle);
		WndProcHandler.WndProcMessage += OnWndProc;

		int pid = Process.GetCurrentProcess().Id;
		_renderProcess = new RenderProcess(pid, _pluginDir, _pluginConfigDir, _dependencyManager, Services.PluginLog);
		_renderProcess.Rpc!.RendererReady += msg =>
		{
			if (!msg.HasDxSharedTexturesSupport)
			{
				Services.PluginLog.Error("Could not initialize shared textures transport. Venuekit cannot show the window.");
				return;
			}
			Services.Framework.RunOnFrameworkThread(CreateOverlay);
		};
		_renderProcess.Rpc.SetCursor += msg =>
		{
			Services.Framework.RunOnFrameworkThread(() =>
			{
				Guid guid = new(msg.Guid.Span);
				if (_overlay != null && _overlay.RenderGuid == guid) _overlay.SetCursor(msg.Cursor);
			});
		};
		_renderProcess.Rpc.UpdateTexture += msg =>
		{
			Services.Framework.RunOnFrameworkThread(() =>
			{
				Guid guid = new(msg.Guid.Span);
				if (_overlay != null && _overlay.RenderGuid == guid) _overlay.SetTexture((IntPtr)msg.TextureHandle);
			});
		};
		_renderProcess.Start();
	}

	private void CreateOverlay()
	{
		if (_renderProcess is null) return;
		_overlay?.Dispose();
		_window.Url = _pendingUrl ?? StartUrl();
		_pendingUrl = null;
		_overlay = new Overlay(_renderProcess, _window, _pluginDir);
		_overlay.Closed += (_, _) => SetVisible(false);
	}

	private (bool, long) OnWndProc(WindowsMessage msg, ulong wParam, long lParam)
	{
		if (_overlay == null || _window.Hidden) return (false, 0);
		return _overlay.WndProcMessage(msg, wParam, lParam);
	}

	private void SetVisible(bool visible)
	{
		_window.Hidden = !visible;
		_config.Visible = visible;
		Save();
	}

	private void Toggle() => SetVisible(_window.Hidden);

	// open the window on a page (e.g. a duel). Before the browser is ready, it opens there later.
	private void OpenUrl(string url)
	{
		SetVisible(true);
		if (_overlay == null) { _pendingUrl = url; return; }
		_window.Url = url;
		_overlay.Navigate(url);
	}

	private void OpenDuel(string id) => OpenUrl(StartUrl("&clash=" + Uri.EscapeDataString(id)));

	private void Render()
	{
		_dependencyManager.Render();
		RenderSettings();

		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
		_renderProcess?.EnsureRenderProcessIsAlive();
		_overlay?.Render();
		ImGui.PopStyleVar();
	}

	// ---------------- settings (small ImGui window) ----------------
	private void RenderSettings()
	{
		if (!_settingsOpen) return;
		ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.FirstUseEver);
		if (!ImGui.Begin("Venuekit settings###vksettings", ref _settingsOpen, ImGuiWindowFlags.NoCollapse))
		{
			ImGui.End();
			return;
		}

		ImGui.TextWrapped(_api.Linked
			? "Connected to Venuekit ✓" + (_charName != "" ? "  (" + _charName + " @ " + _charWorld + ")" : "")
			: "Not connected yet – open the window with /clash and log in with Discord.");
		ImGui.Separator();

		bool vis = !_window.Hidden;
		if (ImGui.Checkbox("Show the Venue Clash window", ref vis)) SetVisible(vis);

		bool locked = _config.Locked;
		if (ImGui.Checkbox("Lock position and size", ref locked)) { _config.Locked = locked; _window.Locked = locked; Save(); }

		float zoom = _config.Zoom;
		if (ImGui.SliderFloat("Zoom", ref zoom, 50f, 200f, "%.0f%%")) { _config.Zoom = zoom; _window.Zoom = zoom; Save(); _overlay?.Zoom(zoom); }

		float op = _config.Opacity;
		if (ImGui.SliderFloat("Opacity", ref op, 30f, 100f, "%.0f%%")) { _config.Opacity = op; _window.Opacity = op; Save(); }

		bool muted = _config.Muted;
		if (ImGui.Checkbox("Mute sounds", ref muted)) { _config.Muted = muted; _window.Muted = muted; Save(); _overlay?.Mute(muted); }

		bool notes = _config.Notifications;
		if (ImGui.Checkbox("Pop-up when somebody challenges me", ref notes)) { _config.Notifications = notes; Save(); }

		bool chat = _config.ChatMessages;
		if (ImGui.Checkbox("Message in the chat log too", ref chat)) { _config.ChatMessages = chat; Save(); }

		ImGui.Separator();
		if (ImGui.Button("Reload page")) OpenUrl(StartUrl());
		ImGui.SameLine();
		if (ImGui.Button("Open in my browser")) Process.Start(new ProcessStartInfo(_config.BaseUrl.TrimEnd('/') + "/cards") { UseShellExecute = true });
		ImGui.SameLine();
		if (ImGui.Button("Disconnect"))
		{
			_config.Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
			Save();
			_api.SetKey(_config.Key);
			OpenUrl(StartUrl());
			Print("Disconnected. Log in again in the Venue Clash window to connect.");
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Right-click a player → \"Challenge to Venue Clash\", or target them and type /clash duel.");
		ImGui.End();
	}

	// ---------------- character + in-game invites ----------------
	private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
	{
		long now = Environment.TickCount64;
		if (now - _lastCharCheck < 2000) return;
		_lastCharCheck = now;
		IPlayerCharacter? me = Services.ObjectTable.LocalPlayer;
		string name = me?.Name.TextValue ?? "", world = me != null ? WorldName(me.HomeWorld.RowId, me.HomeWorld.ValueNullable?.Name.ExtractText()) : "";
		if (name != _charName || world != _charWorld)
		{
			_charName = name;
			_charWorld = world;
			_api.SetCharacter(name, world);
		}
	}

	private static string WorldName(uint id, string? name) => id == 0 || string.IsNullOrEmpty(name) ? "" : name;

	private void OnMenuOpened(IMenuOpenedArgs args)
	{
		if (args.MenuType != ContextMenuType.Default || args.Target is not MenuTargetDefault t) return;
		string name = t.TargetName;
		var hw = t.TargetHomeWorld;
		if (string.IsNullOrEmpty(name) || hw.RowId == 0 || name == _charName) return;
		string world = WorldName(hw.RowId, hw.ValueNullable?.Name.ExtractText());
		if (world == "") return;
		args.AddMenuItem(new MenuItem
		{
			Name = "Challenge to Venue Clash",
			PrefixChar = 'V',
			PrefixColor = 37,
			OnClicked = _ => Invite(name, world),
		});
		args.AddMenuItem(new MenuItem
		{
			Name = "Challenge to Sunset Showdown",
			PrefixChar = 'V',
			PrefixColor = 500,
			OnClicked = _ => Invite(name, world, true),
		});
	}

	private void Invite(string name, string world, bool showdown = false)
	{
		if (!_api.Linked)
		{
			Print("Connect Venuekit first: type /clash and log in with Discord.");
			SetVisible(true);
			return;
		}
		Print("Sending a " + (showdown ? "Sunset Showdown" : "Venue Clash") + " challenge to " + name + "…");
		_ = Task.Run(async () =>
		{
			var (ok, id, error) = await _api.Invite(name, world, showdown);
			await Services.Framework.RunOnFrameworkThread(() =>
			{
				if (ok)
				{
					Print((showdown ? "🌅" : "⚔") + " Challenge sent to " + name + "! Pick your " + (showdown ? "3" : "5") + " cards – the window opens now.");
					if (showdown) OpenUrl(StartUrl("&sd=" + Uri.EscapeDataString(id))); else OpenDuel(id);
				}
				else PrintError(error);
			});
		});
	}

	private void OnNote(VenuekitApi.Note note)
	{
		Services.Framework.RunOnFrameworkThread(() =>
		{
			if (note.T != "duel" && note.T != "sd" && note.T != "sdgo") return;
			bool sd = note.T != "duel";
			string text = note.T == "sdgo" ? note.From + " is ready – your Sunset Showdown starts!" : note.From + " challenges you to " + (sd ? "Sunset Showdown!" : "Venue Clash!");
			if (_config.ChatMessages) Print("⚔ " + text + " Type /clash accept to open it.");
			_lastInvite = note.Id; _lastInviteSd = sd;
			if (!_config.Notifications) return;
			IActiveNotification n = Services.Notifications.AddNotification(new Notification
			{
				Title = "Venue Clash",
				Content = text + "\nClick here to open the duel.",
				Type = NotificationType.Info,
				Minimized = false,
				InitialDuration = TimeSpan.FromSeconds(30),
			});
			string id = note.Id;
			n.Click += _ => { Services.Framework.RunOnFrameworkThread(() => { if (sd) OpenUrl(StartUrl("&sd=" + Uri.EscapeDataString(id))); else OpenDuel(id); }); n.DismissNow(); };
		});
	}

	private string _lastInvite = "";
	private bool _lastInviteSd;

	// ---------------- Sunset Showdown: the plugin rolls /random and reads the result ----------------
	private string _rolledKey = "";
	private long _rolledAt;

	private void OnRollRequest(VenuekitApi.SdRequest q)
	{
		string key = q.Id + ":" + q.Round;
		long now = Environment.TickCount64;
		if (key == _rolledKey && now - _rolledAt < 45000) return;   // already rolled (Venuekit waits for the confirmation)
		_rolledKey = key;
		_rolledAt = now;
		Services.Framework.RunOnFrameworkThread(() => SendRandom(q.Max));
	}

	private static unsafe void SendRandom(int max)
	{
		if (max < 1 || max > 999) return;
		var s = Utf8String.FromString("/random " + max);
		UIModule.Instance()->ProcessChatBoxEntry(s);
		s->Dtor(true);
	}

	private static readonly Regex _nums = new(@"\d+", RegexOptions.Compiled);

	private void OnChat(Dalamud.Game.Chat.IHandleableChatMessage m)
	{
		try
		{
			if (m.LogKind != XivChatType.RandomNumber || !_api.Linked) return;
			var ns = _nums.Matches(m.Message.TextValue);
			if (ns.Count < 2) return;   // /random without a number has no "(out of N)"
			int roll = int.Parse(ns[ns.Count - 2].Value), max = int.Parse(ns[ns.Count - 1].Value);
			if (roll < 1 || max < 1 || roll > max || max > 999) return;
			string who;
			var pp = m.Message.Payloads.OfType<PlayerPayload>().FirstOrDefault();
			if (pp != null)
			{
				string w = pp.World.ValueNullable?.Name.ExtractText() ?? "";
				if (w == "") return;
				who = pp.PlayerName + "@" + w;
			}
			else
			{
				if (_charName == "" || _charWorld == "") return;
				who = _charName + "@" + _charWorld;   // "You roll …"
			}
			var list = new List<(string, int, int)> { (who, roll, max) };
			_ = Task.Run(() => _api.ReportRolls(list));
		}
		catch (Exception e) { Services.PluginLog.Warning("[venuekit] chat roll: " + e.Message); }
	}

	private void HandleCommand(string command, string rawArgs)
	{
		string[] args = rawArgs.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
		string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
		switch (sub)
		{
			case "":
				Toggle();
				break;
			case "duel":
			case "challenge":
				if (Services.TargetManager.Target is IPlayerCharacter pc && pc.Name.TextValue != _charName)
				{
					string w = WorldName(pc.HomeWorld.RowId, pc.HomeWorld.ValueNullable?.Name.ExtractText());
					if (w != "") { Invite(pc.Name.TextValue, w); break; }
				}
				PrintError("Target a player first, then type /clash duel.");
				break;
			case "accept":
				if (_lastInvite != "" && _lastInviteSd) OpenUrl(StartUrl("&sd=" + Uri.EscapeDataString(_lastInvite)));
				else if (_lastInvite != "") OpenDuel(_lastInvite);
				else PrintError("No open challenge right now.");
				break;
			case "settings":
			case "config":
				_settingsOpen = true;
				break;
			case "reload":
				OpenUrl(StartUrl());
				break;
			default:
				Print("/clash – open/close Venue Clash · /clash duel – challenge your target · /clash accept – open the last challenge · /clash settings");
				break;
		}
	}

	private static void Print(string msg) => Services.Chat.Print("[Venuekit] " + msg);
	private static void PrintError(string msg) => Services.Chat.PrintError("[Venuekit] " + msg);
}
