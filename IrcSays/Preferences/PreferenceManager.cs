﻿using System;
using System.IO;
using IrcSays.Utility;

namespace IrcSays.Preferences
{
	public static class PreferenceManager
	{
		private static FileSystemPropertyService _properties;

		public static void Initialize()
		{
			FileSystemPropertyService.LockKey = "IrcSays-5C63666E-CDB6-41A0-898C-3CD18EFDFC13";

			var propsBasePath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"IrcSays");

			var configPath = new DirectoryName(Path.Combine(propsBasePath, "Config"));
			var dataPath = new DirectoryName(Path.Combine(propsBasePath, "Data"));
			_properties = new FileSystemPropertyService(configPath, dataPath, "IrcSaysProperties");
		}
	}
}