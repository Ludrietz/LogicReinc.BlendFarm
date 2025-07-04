﻿using LogicReinc.BlendFarm.Objects;
using LogicReinc.BlendFarm.Server;
using LogicReinc.BlendFarm.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using static LogicReinc.BlendFarm.BlendFarmSettings;
using static LogicReinc.BlendFarm.Windows.RenderWindow;

namespace LogicReinc.BlendFarm
{
    public class BlendFarmSettings
    {
        private static string FILE_NAME = "ClientSettings";


        /// <summary>
        /// Used to store copies of used blend files
        /// </summary>
        public string LocalBlendFiles { get; set; } = "LocalBlendFiles";


        /// <summary>
        /// Determines if the application should listen for servers over udp broadcasts
        /// </summary>
        public bool ListenForBroadcasts { get; set; } = true;

        public OpenBlenderProject.UISettings UISettings { get; set; } = null;

        /// <summary>
        /// Previously used version
        /// </summary>
        public string LastVersion { get; set; }

        /// <summary>
        /// Previously used blend files
        /// </summary>
        public List<HistoryEntry> History { get; set; } = new List<HistoryEntry>();
        /// <summary>
        /// Clients from previous sessions
        /// </summary>
        public Dictionary<string, HistoryClient> PastClients { get; set; } = new Dictionary<string, HistoryClient>();

        public DateTime LastAnnouncementDate { get; set; } = DateTime.MinValue;

        public Dictionary<string, UIProjectSettings> ProjectSettings { get; set; } = new Dictionary<string, UIProjectSettings>();


        public bool Option_UseAssetsSync { get; set; }
        public bool Option_ConnectLocal { get; set; }
        public bool Option_ImportSettings { get; set; }


        public void ApplyProjectSettings(string fileName, UIProjectSettings settings)
        {
            if (ProjectSettings.ContainsKey(fileName))
                ProjectSettings[fileName] = settings;
            else
                ProjectSettings.Add(fileName, settings);
            Save();
        }
        public UIProjectSettings GetProjectSettings(string file)
        {
            if (ProjectSettings.ContainsKey(file))
                return ProjectSettings[file];
            return null;
        }


        #region Boilerplate

        private static BlendFarmSettings _instance = null;
        public static BlendFarmSettings Instance
        {
            get
            {
                if (_instance == null)
                    _instance = Load();
                return _instance;
            }
        }

        public void Save()
        {
            File.WriteAllText(SystemInfo.RelativeToApplicationDirectory(FILE_NAME), JsonSerializer.Serialize(this));
        }
        public static BlendFarmSettings Load()
        {
            string path = SystemInfo.RelativeToApplicationDirectory(FILE_NAME);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<BlendFarmSettings>(File.ReadAllText(path));
            else
                return new BlendFarmSettings();
        }
        #endregion


        public class HistoryClient
        {
            public string Name { get; set; }
            public string Address { get; set; }

            public RenderType RenderType { get; set; } = RenderType.CPU;
            public double Performance { get; set; }
            public string Pass { get; set; }
            public string MAC { get; set; }
        }

        /// <summary>
        /// Used to keep track of previously used blend files
        /// </summary>
        public class HistoryEntry
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public DateTime Date { get; set; }
        }



        public class UIProjectSettings
        {
            public bool UseNetworked { get; set; }
            public string NetworkPathWindows { get; set; }
            public string NetworkPathLinux { get; set; }
            public string NetworkPathMacOS { get; set; }
        }
    }
}
