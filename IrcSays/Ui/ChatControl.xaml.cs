﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IrcSays.Application;
using IrcSays.Communication.Irc;
using IrcSays.Configuration;

namespace IrcSays.Ui
{
	public partial class ChatControl : ChatPage
	{
		#region Nested types

		private class CommandException : Exception
		{
			public CommandException(string message)
				: base(message)
			{
			}
		}

		#endregion

		private const double MinNickListWidth = 50.0;

		private readonly LinkedList<string> _history;
		private LinkedListNode<string> _historyNode;
		private readonly LogFileHandle _logFile;
		private ChatLine _markerLine;
		private Timer _delayTimer;

		public ChatControl(ChatPageType type, IrcSession session, IrcTarget target)
			: base(type, session, target, type == ChatPageType.Server
				? "server"
				: (type == ChatPageType.DccChat
					? "dcc-chat"
					: string.Format("{0}.{1}", session.NetworkName, target.Name).ToLowerInvariant()))
		{
			_history = new LinkedList<string>();
			_nickList = new NicknameList();

			InitializeComponent();

			var state = App.Settings.Current.Windows.States[Id];
			if (Type == ChatPageType.DccChat)
			{
				Header = string.Format("[CHAT] {0}", Target.Name);
			}
			else if (Type == ChatPageType.Chat ||
					Type == ChatPageType.Server)
			{
				Header = Target == null ? "Server" : Target.ToString();
				SubscribeEvents();

				if (!IsServer)
				{
					_logFile = App.OpenLogFile(Id);
					var logLines = new List<ChatLine>();
					while (_logFile.Buffer.Count > 0)
					{
						var cl = _logFile.Buffer.Dequeue();
						cl.Marker = _logFile.Buffer.Count == 0 ? ChatMarker.OldMarker : ChatMarker.None;
						logLines.Add(cl);
					}
					boxOutput.AppendBulkLines(logLines);
				}

				if (IsChannel)
				{
					colNickList.MinWidth = MinNickListWidth;
					colNickList.Width = new GridLength(state.NickListWidth);

					Write("Join", string.Format("Now talking on {0}", Target.Name));
					Session.AddHandler(new IrcCodeHandler(e =>
					{
						if (e.Message.Parameters.Count > 2 &&
							Target.Equals(new IrcTarget(e.Message.Parameters[1])))
						{
							_channelModes = e.Message.Parameters[2].ToCharArray().Where(c => c != '+').ToArray();
							SetTitle();
						}
						e.Handled = true;
						return true;
					}, IrcCode.RplChannelModeIs));
					Session.Mode(Target);
					splitter.IsEnabled = true;

					var nameHandler = new IrcCodeHandler(e =>
					{
						if (e.Message.Parameters.Count >= 3)
						{
							var to = new IrcTarget(e.Message.Parameters[e.Message.Parameters.Count - 2]);
							if (Target.Equals(to))
							{
								_nickList.AddRange(e.Message.Parameters[e.Message.Parameters.Count - 1].Split(' ').
									Where(n => n.Length > 0));
							}
						}
						e.Handled = true;
						return false;
					}, IrcCode.RplNameReply);
					Session.AddHandler(nameHandler);
					Session.AddHandler(new IrcCodeHandler(e =>
					{
						Session.RemoveHandler(nameHandler);
						e.Handled = true;
						return true;
					}, IrcCode.RplEndOfNames));
				}
				else if (IsNickname)
				{
					_prefix = Target.Name;
				}
			}
			else
			{
				throw new ArgumentException("Page type is not supported.");
			}

			boxOutput.ColumnWidth = state.ColumnWidth;

			Loaded += ChatControl_Loaded;
			Unloaded += ChatControl_Unloaded;
			PrepareContextMenus();
			boxOutput.ContextMenu = GetDefaultContextMenu();
		}

		public bool IsChannel
		{
			get { return Type == ChatPageType.Chat && Target.IsChannel; }
		}

		public bool IsNickname
		{
			get { return Type == ChatPageType.Chat && !Target.IsChannel; }
		}

		public string Perform { get; set; }

		public static readonly DependencyProperty IsConnectedProperty =
			DependencyProperty.Register("IsConnected", typeof (bool), typeof (ChatControl));

		public bool IsConnected
		{
			get { return (bool) GetValue(IsConnectedProperty); }
			set { SetValue(IsConnectedProperty, value); }
		}

		public static readonly DependencyProperty SelectedLinkProperty =
			DependencyProperty.Register("SelectedLink", typeof (string), typeof (ChatControl));

		public string SelectedLink
		{
			get { return (string) GetValue(SelectedLinkProperty); }
			set { SetValue(SelectedLinkProperty, value); }
		}

		public void Connect(ServerElement server)
		{
			Session.AutoReconnect = false;
			Perform = server.OnConnect;
			Connect(server.Hostname, server.Port, server.IsSecure, server.AutoReconnect, server.Password);
		}

		public void Connect(string server, int port, bool useSsl, bool autoReconnect, string password)
		{
			Session.Open(server, port, useSsl,
				!string.IsNullOrEmpty(Session.Nickname)
					? Session.Nickname
					: App.Settings.Current.User.Nickname,
				App.Settings.Current.User.Username,
				App.Settings.Current.User.FullName,
				autoReconnect,
				password,
				App.Settings.Current.User.Invisible,
				App.Settings.Current.Dcc.FindExternalAddress,
				App.ProxyInfo);
		}

		private void ParseInput(string text)
		{
			Execute(text, (Keyboard.Modifiers & ModifierKeys.Shift) > 0);
		}

		private void Write(string styleKey, int nickHashCode, string nick, string text, bool attn)
		{
			var cl = new ChatLine(styleKey, nickHashCode, nick, text, ChatMarker.None);

			if (_hasDeactivated)
			{
				_hasDeactivated = false;
				if (_markerLine != null)
				{
					_markerLine.Marker &= ~ChatMarker.NewMarker;
				}
				_markerLine = cl;
				cl.Marker = ChatMarker.NewMarker;
			}

			if (attn)
			{
				cl.Marker |= ChatMarker.Attention;
			}

			if (VisualParent == null)
			{
				if (IsNickname || Type == ChatPageType.DccChat)
				{
					// Activity in PM window
					NotifyState = NotifyState.Alert;
				}
				else if (!string.IsNullOrEmpty(nick) &&
						NotifyState != NotifyState.Alert)
				{
					// Chat activity in channel
					NotifyState = NotifyState.ChatActivity;
				}
				else if (NotifyState == NotifyState.None)
				{
					// Other activity in channel / server
					NotifyState = NotifyState.NoiseActivity;
				}
			}

			boxOutput.AppendLine(cl);
			if (_logFile != null)
			{
				_logFile.WriteLine(cl);
			}
		}

		private void Write(string styleKey, IrcPeer peer, string text, bool attn)
		{
			Write(styleKey, string.Format("{0}@{1}", peer.Username, peer.Hostname).GetHashCode(),
				GetNickWithLevel(peer.Nickname), text, attn);
			if (!boxOutput.IsAutoScrolling)
			{
				App.DoEvent("beep");
			}
		}

		private void Write(string styleKey, string text)
		{
			Write(styleKey, 0, null, text, false);
		}

		private void SetInputText(string text)
		{
			txtInput.Text = text;
			txtInput.SelectionStart = text.Length;
			_nickCandidates = null;
		}

		private void SetTitle()
		{
			var userModes = Session.UserModes.Length > 0
				? string.Format("+{0}", string.Join("", (from c in Session.UserModes select c.ToString()).ToArray()))
				: "";
			var channelModes = _channelModes.Length > 0
				? string.Format("+{0}", string.Join("", (from c in _channelModes select c.ToString()).ToArray()))
				: "";

			switch (Type)
			{
				case ChatPageType.DccChat:
					Title = string.Format("{0} - {1} - DCC chat with {2}", App.Product, Session.Nickname, Target.Name);
					break;

				case ChatPageType.Server:
					if (Session.State == IrcSessionState.Disconnected)
					{
						Title = string.Format("{0} - Not Connected", App.Product);
					}
					else
					{
						Title = string.Format("{0} - {1} ({2}) on {3}", App.Product, Session.Nickname,
							userModes, Session.NetworkName);
					}
					break;

				default:
					if (Target.IsChannel)
					{
						Title = string.Format("{0} - {1} ({2}) on {3} - {4} ({5}) - {6}", App.Product, Session.Nickname,
							userModes, Session.NetworkName, Target, channelModes, _topic);
					}
					else
					{
						Title = string.Format("{0} - {1} ({2}) on {3} - {4}", App.Product, Session.Nickname,
							userModes, Session.NetworkName, _prefix);
					}
					break;
			}
		}

		private void SubmitInput()
		{
			var text = txtInput.Text;
			txtInput.Clear();
			if (_history.Count == 0 ||
				_history.First.Value != text)
			{
				_history.AddFirst(text);
			}
			while (_history.Count > App.Settings.Current.Buffer.InputHistory)
			{
				_history.RemoveLast();
			}
			_historyNode = null;
			ParseInput(text);
		}

		private ContextMenu GetDefaultContextMenu()
		{
			if (IsServer)
			{
				var menu = Resources["cmServer"] as ContextMenu;
				var item = menu.Items[0] as MenuItem;
				if (item != null)
				{
					item.Items.Refresh();
					item.IsEnabled = item.Items.Count > 0;
				}
				return menu;
			}
			return Resources["cmChannel"] as ContextMenu;
		}

		public override void Dispose()
		{
			var state = App.Settings.Current.Windows.States[Id];
			state.ColumnWidth = boxOutput.ColumnWidth;

			if (Type == ChatPageType.DccChat &&
				_dcc != null)
			{
				_dcc.Dispose();
				DeletePortForwarding();
			}
			else
			{
				if (IsChannel)
				{
					state.NickListWidth = colNickList.ActualWidth;
				}
				UnsubscribeEvents();
				if (_logFile != null)
				{
					_logFile.Dispose();
				}
			}
		}

		private void DoPerform(int startIndex)
		{
			var commands =
				Perform.Split(Environment.NewLine.ToCharArray()).Where(s => s.Trim().Length > 0).Select(s => s.Trim()).ToArray();
			for (var i = startIndex; i < commands.Length; i++)
			{
				if (commands[i].StartsWith("/DELAY", StringComparison.InvariantCultureIgnoreCase))
				{
					int time;
					var parts = commands[i].Split(' ');
					if (parts.Length < 2 ||
						!int.TryParse(parts[1], out time))
					{
						time = 1;
					}
					_delayTimer = new Timer(o => { Dispatcher.BeginInvoke((Action) (() => { DoPerform(i + 1); })); }, null,
						time * 1000, Timeout.Infinite);
					return;
				}
				Execute(commands[i], false);
			}
		}
	}
}