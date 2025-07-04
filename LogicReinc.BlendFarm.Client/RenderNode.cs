﻿using LogicReinc.BlendFarm.Client.Exceptions;
using LogicReinc.BlendFarm.Shared;
using LogicReinc.BlendFarm.Shared.Communication;
using LogicReinc.BlendFarm.Shared.Communication.RenderNode;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LogicReinc.BlendFarm.Client
{
    /// <summary>
    /// Top-level class to interact with an individual node
    /// Also has some PropertyChanged events for UI (not all)
    /// </summary>
    public class RenderNode : INotifyPropertyChanged
    {
        //Used to specify the minimum server required version
        //Modified when protocol changes
        public const int MinumumVersionMajor = 1;
        public const int MinimumVersionMinor = 1;
        public const int MinimumVersionPatch = 3;

        private StringBuilder _currentLog = new StringBuilder();

        //Info
        /// <summary>
        /// Name of node
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Network address of node
        /// </summary>
        public string Address { get; set; }
        /// <summary>
        /// Current status of node
        /// </summary>
        public string Status { get; set; }
        /// <summary>
        /// Operating system of node
        /// </summary>
        public string OS { get; set; }

        public string Pass { get; set; }

        /// <summary>
        /// Networking client for node
        /// </summary>
        private RenderClient Client { get; set; }

        //ClientDerived
        /// <summary>
        /// Computer name of node
        /// </summary>
        public string ComputerName { get; set; }
        /// <summary>
        /// Number of CPU Logical cores
        /// </summary>
        public int Cores { get; set; } = -1;

        /// <summary>
        /// Performance based on cores or user-defined
        /// </summary>
        public double Performance { get; set; } = 0;
        /// <summary>
        /// Auto-balanced Performance score
        /// </summary>
        public decimal PerformanceScorePP { get; set; } = 0;
        public string MAC { get; set; }

        /// <summary>
        /// Render type this node will use
        /// </summary>
        public RenderType RenderType { get; set; } = RenderType.CPU;

        //State
        /// <summary>
        /// If node is not doing anything
        /// </summary>
        public bool IsIdle => string.IsNullOrEmpty(Activity);
        /// <summary>
        /// If node is connected
        /// </summary>
        public bool Connected => Client != null && Client.Connected;

        public string SelectedSessionID { get; private set; }

        /// <summary>
        /// FileID of the version of the file this node has synced
        /// </summary>
        public long LastFileID { get; set; } = -1;
        /// <summary>
        /// Descriptive name of current activity
        /// </summary>
        public string Activity { get; set; } = null;
        /// <summary>
        /// Progress of current activity
        /// </summary>
        public double ActivityProgress { get; set; } = 0;
        /// <summary>
        /// If activity has any progress
        /// </summary>
        public bool HasActivityProgress => ActivityProgress > 0;

        /// <summary>
        /// Last exception
        /// </summary>
        public string Exception { get; set; } = null;
        /// <summary>
        /// Last status
        /// </summary>
        public string LastStatus { get; set; }

        /// <summary>
        /// If the node is prepared with the required Blender version
        /// </summary>
        public bool IsPrepared { get; set; }
        /// <summary>
        /// If the node has synced up with the latest file
        /// </summary>
        public bool IsSynced => IsSessionSynced(SelectedSessionID);

        public Dictionary<string, bool> SyncedMap { get; set; } = new Dictionary<string, bool>();

        /// <summary>
        /// Current Task ID
        /// </summary>
        public string CurrentTask { get; set; }
        private CancellationTokenSource _taskCancelToken = null;

        /// <summary>
        /// Blender Versions that have been checked and are available
        /// </summary>
        public List<string> AvailableVersions { get; set; } = new List<string>();


        //Events
        /// <summary>
        /// Event on node connected
        /// </summary>
        public event Action<RenderNode> OnConnected;
        /// <summary>
        /// Event on node disconnected
        /// </summary>
        public event Action<RenderNode> OnDisconnected;

        /// <summary>
        /// Event whenever Activity changed
        /// </summary>
        public event Action<RenderNode, string> OnActivityChanged;
        /// <summary>
        /// Event whenever FileID changed
        /// </summary>
        public event Action<RenderNode, long> OnFileIDChanged;
        /// <summary>
        /// Event whenever a BatchResult came in
        /// </summary>
        public event Action<RenderNode, RenderBatchResult> OnBatchResult;

        public event Action<RenderNode, string> OnLog;

        /// <summary>
        /// Whenever a relevant property changed
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;


        public RenderNode()
        {
            OnConnected += (n) => 
                TriggerPropChange(nameof(Connected));
            OnDisconnected += (n) =>
            {
                foreach (string k in SyncedMap.Keys.ToList())
                    UpdateSyncedStatus(k, false);
                TriggerPropChange(nameof(Connected));
            };
        }

        public void SetPassword(string pass)
        {
            Pass = pass;
        }

        public void UpdatePerformance(int pixelsRendered, int ms)
        {
            decimal msPerPixel = (decimal)((decimal)pixelsRendered / ms);
            PerformanceScorePP = msPerPixel;
        }

        public string GetCurrentLog()
        {
            return _currentLog.ToString();
        }

        //Connection
        /// <summary>
        /// Connects to node and prepare version
        /// </summary>
        public async Task ConnectAndPrepare(string version)
        {
            await Connect();

            if (Connected)
                await PrepareVersion(version);
        }
        /// <summary>
        /// Connect to node
        /// </summary>
        public async Task Connect()
        {
            if (!Connected)
            {
                UpdateActivity("Connecting");
                if (MAC != null) MagicPacket.Send(MAC);

                try
                {
                    _currentLog.Clear();
                    Client = await RenderClient.Connect(Address);
                    Client.OnConnected += (a) => OnConnected?.Invoke(this);
                    Client.OnDisconnected += (a) => OnDisconnected?.Invoke(this);
                    Client.OnPacket += HandlePacket;

                    CheckProtocolResponse protocolResp = null;
                    try
                    {
                        protocolResp = await CheckProtocol(MinumumVersionMajor, MinimumVersionMinor, MinimumVersionPatch, Protocol.Version);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("Outdated protocol, exception during check (" + ex.Message + ")");
                    }
                    if (protocolResp == null || Protocol.Version != protocolResp.ProtocolVersion)
                        throw new InvalidOperationException($"Outdated protocol, update node before connecting (Protocol: {Protocol.Version}, Found: {protocolResp?.ProtocolVersion})");

                    if(protocolResp.RequireAuth)
                    {
                        try
                        {
                            AuthResponse resp = await Client.Send<AuthResponse>(new AuthRequest()
                            {
                                Pass = Pass
                            }, CancellationToken.None);
                            if(!resp.IsAuthenticated)
                                throw new InvalidDataException("Authentication failed");
                        }
                        catch(Exception ex)
                        {
                            throw new InvalidDataException($"Authentication failed ({ex.Message})");
                        }
                    }


                    ComputerInfoResponse compData = await GetComputerInfo();
                    OS = compData.OS;
                    Cores = compData.Cores;
                    ComputerName = compData.Name;

                    UpdateException("");
                    OnConnected?.Invoke(this);
                }
                catch(Exception ex)
                {
                    UpdateException(ex.Message);
                    Client = null;
                    throw;
                }
                finally
                {
                    UpdateActivity("");
                }
            }
        }
        /// <summary>
        /// Attempts to recover a session connection
        /// </summary>
        public async Task<RecoverResponse> ConnectRecover(string[] sessions)
        {
            if (Connected)
                throw new InvalidOperationException("Already connected");
            await Connect();
            return await Recover(sessions);
        }
        /// <summary>
        /// Attempts to recover a session connection n attempts
        /// </summary>
        public async Task<RecoverResponse> ConnectRecover(int attempts, int interval, string[] sessions)
        {
            if (Connected)
                throw new InvalidOperationException("Already connected");
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    await Connect();
                    return await Recover(sessions);
                }
                catch(Exception ex)
                {
                    Console.WriteLine($"Recover failed due to: {ex.Message} ({i+1}/{attempts})");
                }
                await Task.Delay(interval);
            }
            throw new RecoverException("Failed to recover connection");
        }

        /// <summary>
        /// Disconnect from node
        /// </summary>
        public void Disconnect()
        {
            if (Connected)
                Client.Disconnect();
            Client = null;
            UpdateException("");
            TriggerPropChange(nameof(Connected));
        }

        /// <summary>
        /// Handle package from client
        /// </summary>
        private void HandlePacket(RenderClient client, BlendFarmMessage p)
        {
            if (p is RenderInfoResponse)
            {
                RenderInfoResponse renderResp = ((RenderInfoResponse)p);

                double progress = Math.Round(((double)renderResp.TilesFinished / renderResp.TilesTotal) * 100, 1);
                if (renderResp.TaskID == CurrentTask)
                    UpdateActivity($"Rendering ({renderResp.TilesFinished}/{renderResp.TilesTotal})", progress);
            }
            if(p is RenderBatchResult)
            {
                RenderBatchResult renderBatchResult = ((RenderBatchResult)p);

                OnBatchResult?.Invoke(this, renderBatchResult);
               
            }
            if(p is BlendFarmDisconnected packDisconnected)
            {
                if (packDisconnected.IsError)
                    UpdateException(packDisconnected.Reason);
            }
            if(p is ActivityRequest packAct)
            {
                UpdateActivity(packAct.Activity, packAct.Progress);
            }
            if(p is ConsoleActivityResponse packConsole)
            {
                _currentLog.AppendLine(packConsole.Output);
                OnLog?.Invoke(this, packConsole.Output);
            }
        }

        //Remote Tasks

        public async Task<RecoverResponse> Recover(string[] sessions)
        {
            return await Client.Send<RecoverResponse>(new RecoverRequest()
            {
                SessionIDs = sessions
            }, CancellationToken.None);
        }

        public async Task<CheckProtocolResponse> CheckProtocol(int major, int minor, int patch, int version)
        {
            return await Client.Send<CheckProtocolResponse>(new CheckProtocolRequest()
            {
                ClientVersionMajor = major,
                ClientVersionMinor = minor,
                ClientVersionPatch = patch,
                ProtocolVersion = version
            }, CancellationToken.None);
        }

        public void RequestConsoleActivityRedirect()
        {
            Client.Send(new ConsoleActivityRequest());
        }


        /// <summary>
        /// Prepare a version of blender on node
        /// </summary>
        public async Task<PrepareResponse> PrepareVersion(string version)
        {
            if (Client == null)
                throw new InvalidOperationException("Client not connected");

            UpdateActivity("Downloading Version");

            PrepareResponse resp = await Client.Send<PrepareResponse>(new PrepareRequest()
            {
                Version = version
            }, CancellationToken.None);

            if (resp != null && resp.Success)
            {
                if (!AvailableVersions.Contains(version))
                    AvailableVersions.Add(version);
                UpdatePreparedStatus(true);
            }
            else
                UpdateException($"Failed Version {version}");

            UpdateActivity("");

            return resp;
        }

        public async Task<SyncResponse> SyncNetworkFile(string sess, long fileid, string windowsPath, string linuxPath, string macOSPath)
        {
            if (Client == null)
                throw new InvalidOperationException("Client not connected");
            SyncResponse resp = null;
            try
            {
                UpdateActivity("Syncing");

                //Start Sync

                //Initialize Sync
                resp = await Client.Send<SyncResponse>(new SyncNetworkRequest()
                {
                    SessionID = sess,
                    FileID = fileid,
                    WindowsPath = windowsPath,
                    LinuxPath = linuxPath,
                    MacOSPath = macOSPath
                }, CancellationToken.None);

                if (!resp.Success)
                    throw new SyncException(resp.Message);
                if (resp.SameFile)
                {
                    UpdateSyncedStatus(sess, true);
                    return resp;
                }

                //End Sync
                if (await CheckSyncFile(sess, fileid))
                    UpdateSyncedStatus(sess, true);
                else
                    UpdateSyncedStatus(sess, false);
            }
            finally
            {
                UpdateActivity("");
            }

            return resp;
        }

        /// <summary>
        /// Transfer blender file to node
        /// </summary>
        /// <param name="sess">An identifier for the session</param>
        /// <param name="fileid">An identifier used to differentiate versions</param>
        /// <returns></returns>
        public async Task<SyncResponse> SyncFile(string sess, long fileid, Stream file, Compression compression)
        {
            if (Client == null)
                throw new InvalidOperationException("Client not connected");
            SyncResponse resp = null;
            try
            {
                UpdateActivity("Syncing");

                //Start Sync

                //Initialize Sync
                resp = await Client.Send<SyncResponse>(new SyncRequest()
                {
                    SessionID = sess,
                    FileID = fileid,
                    Compression = compression
                }, CancellationToken.None);

                if (!resp.Success)
                    throw new SyncException(resp.Message);
                if (resp.SameFile)
                {
                    UpdateSyncedStatus(sess, true);
                    return resp;
                }

                //Transfer file
                byte[] chunk = new byte[1024 * 1024 * 10];
                int read = 0;
                long written = 0;
                while ((read = file.Read(chunk, 0, chunk.Length)) > 0)
                {
                    byte[] toSend = (read == chunk.Length) ? chunk : chunk.AsSpan(0, read).ToArray();
                    //Send chunk
                    var uploadResp = await Client.Send<SyncUploadResponse>(new SyncUploadRequest()
                    {
                        Data = toSend, //Convert.ToBase64String(chunk, 0, read),
                        UploadID = resp.UploadID,
                        //Hash = Hash.ComputeSyncHash(toSend) //Used during debugging
                    }, CancellationToken.None);

                    if (!uploadResp.Success)
                        throw new SyncException(uploadResp.Message);

                    written += read;

                    double progress = (double)written / file.Length;
                    double p = Math.Round(progress * 100, 1);
                    UpdateActivity($"Syncing ({p}%)", p);
                    //Thread.Sleep(100);
                }

                //Indicate Transfer Complete
                var complete = await Client.Send<SyncCompleteResponse>(new SyncCompleteRequest()
                {
                    UploadID = resp.UploadID
                }, CancellationToken.None);

                //End Sync

                if (await CheckSyncFile(sess, fileid))
                    UpdateSyncedStatus(sess, true);
                else
                    UpdateSyncedStatus(sess, false);
            }
            finally
            {
                UpdateActivity("");
            }

            return resp;
        }
        /// <summary>
        /// Render a batch of RenderSettings
        /// </summary>
        public async Task<RenderBatchResponse> RenderBatch(RenderBatchRequest req)
        {
            if (Client == null)
                throw new InvalidOperationException("Client not connected");
            if (CurrentTask != null)
                throw new InvalidOperationException("Already rendering");

            RenderBatchResponse resp = null;
            _taskCancelToken = new CancellationTokenSource();
            try
            {

                CurrentTask = req.TaskID;

                UpdateActivity("Render Loading..");

                while (true)
                {
                    try
                    {
                        resp = await Client.Send<RenderBatchResponse>(req, _taskCancelToken.Token);
                        break;
                    }
                    catch (BlendFarmDisconnectedException ex)
                    {
                        RecoverResponse r = await ConnectRecover(5, 1000, new string[] { req.SessionID });
                        if (!r.Success)
                            throw new RecoverException(r.Message);
                    }
                    catch (Exception exOther)
                    {
                        throw;
                    }
                }
            }
            finally
            {
                UpdateActivity("");
                CurrentTask = null;
                _taskCancelToken = null;
            }
            return resp;
        }
        /// <summary>
        /// Render a single RenderSettings
        /// </summary>
        public async Task<RenderResponse> Render(RenderRequest req)
        {
            if (Client == null)
                throw new InvalidOperationException("Client not connected");
            RenderResponse resp = null;
            _taskCancelToken = new CancellationTokenSource();
            try
            {
                CurrentTask = req.TaskID;

                UpdateActivity("Render Loading..");

                int recoverAtts = 0;
                while (true) 
                {
                    try
                    {
                        resp = await Client.Send<RenderResponse>(req, _taskCancelToken.Token);
                        break;
                    }
                    catch (BlendFarmDisconnectedException ex)
                    {
                        recoverAtts++;
                        if (recoverAtts > 3)
                            throw new RecoverException($"Failed to recover too many times, connection too unstable");

                        RecoverResponse r = await ConnectRecover(5, 1000, new string[] { req.SessionID });
                        if (!r.Success)
                            throw new RecoverException(r.Message);
                    }
                    catch (Exception exOther)
                    {
                        throw;
                    }
                }

            }
            finally
            {
                UpdateActivity("");
                CurrentTask = null;
                _taskCancelToken = null;
            }

            return resp;
        }
        
        public async Task<BlenderPeekResponse> Peek(BlenderPeekRequest req)
        {
            if (Client == null)
                throw new InvalidOperationException("Client not connected");
            BlenderPeekResponse resp = null;
            _taskCancelToken = new CancellationTokenSource();
            try
            {
                UpdateActivity("Peeking...");

                int recoverAtts = 0;
                while (true)
                {
                    try
                    {
                        resp = await Client.Send<BlenderPeekResponse>(req, _taskCancelToken.Token);
                        break;
                    }
                    catch (BlendFarmDisconnectedException ex)
                    {
                        recoverAtts++;
                        if (recoverAtts > 3)
                            throw new RecoverException($"Failed to recover too many times, connection too unstable");

                        RecoverResponse r = await ConnectRecover(5, 1000, new string[] { req.SessionID });
                        if (!r.Success)
                            throw new RecoverException(r.Message);
                    }
                    catch (Exception exOther)
                    {
                        throw;
                    }
                }

            }
            finally
            {
                UpdateActivity("");
                CurrentTask = null;
                _taskCancelToken = null;
            }

            return resp;
        }


        /// <summary>
        /// Cancels an ongoing render with SesionID
        /// </summary>
        /// <returns></returns>
        public async Task CancelRender(string sessionID)
        {
            if (_taskCancelToken != null)
            {
                _taskCancelToken.Cancel();
                await Task.Run(() =>
                {
                    Client.Send(new CancelRenderRequest()
                    {
                        Session = sessionID
                    });
                    UpdateActivity(Activity, -1);
                });
            }
        }


        //Check Client Data

        /// <summary>
        /// Returns information about this machine
        /// </summary>
        public async Task<ComputerInfoResponse> GetComputerInfo()
        {
            if (!Connected)
                throw new InvalidOperationException("Not connected");

            ComputerInfoResponse resp = await Client.Send<ComputerInfoResponse>(new ComputerInfoRequest(), CancellationToken.None);

            if (resp != null)
            {
                ComputerName = resp.Name;
                Cores = resp.Cores;
                OS = resp.OS;
            }

            return resp;
        }


        public async Task<bool> IsBusy()
        {
            return (await Client.Send<IsBusyResponse>(new IsBusyRequest(), CancellationToken.None))?.IsBusy ?? false;
        }

        /// <summary>
        /// Check if a version is present on node
        /// </summary>
        public async Task<bool> CheckVersion(string version)
        {
            if (Client == null)
                throw new InvalidOperationException("Client not connected");

            if (!AvailableVersions.Contains(version))
            {

                var resp = await Client.Send<IsVersionAvailableResponse>(new IsVersionAvailableRequest()
                {
                    Version = version
                }, CancellationToken.None);
                if (resp.Success)
                {
                    if (!AvailableVersions.Contains(version))
                        AvailableVersions.Add(version);
                    return true;
                }
                return false;
            }
            else
                return true;
        }
        /// <summary>
        /// Check if provided file id is the current version of the file
        /// </summary>
        /// <param name="sess">An identifier for the session</param>
        /// <param name="id">An identifier for the file(.blend)</param>
        /// <returns></returns>
        public async Task<bool> CheckSyncFile(string sess, long id)
        {
            if (Client == null)
                throw new InvalidOperationException("Client not connected");


            var resp = await Client.Send<CheckSyncResponse>(new CheckSyncRequest()
            {
                FileID = id,
                SessionID = sess
            }, CancellationToken.None);

            if (resp?.Success ?? false)
            {
                UpdateFileID(id);
                UpdateSyncedStatus(sess, true);
                return true;
            }
            else
                return false;
        }


        //PropertyChanges (generally for UI)
        public void UpdateActivity(string activity, double progress = -1)
        {
            if (Activity != activity)
            {
                ActivityProgress = -1;
                Activity = activity;
                OnActivityChanged?.Invoke(this, activity);
                TriggerPropChange(nameof(Activity), nameof(IsIdle));
            }
            if(ActivityProgress != progress)
            {
                ActivityProgress = progress;
                TriggerPropChange(nameof(ActivityProgress), nameof(HasActivityProgress));
            }
        }

        public bool IsSessionSynced(string sessionID)
        {
            if (sessionID == null)
                return false;

            bool sessionSynced = false;
            lock (SyncedMap)
                sessionSynced = SyncedMap.ContainsKey(sessionID) ? SyncedMap[sessionID] : false;
            return sessionSynced;
        }

        public void UpdateFileID(long id)
        {
            if (LastFileID != id)
            {
                LastFileID = id;
                OnFileIDChanged?.Invoke(this, id);
                TriggerPropChange(nameof(LastFileID));
            }
        }
        public void UpdateException(string excp)
        {
            if (Exception != excp)
            {
                Exception = excp;
                TriggerPropChange(nameof(Exception));
            }
        }
        public void UpdateLastStatus(string status)
        {
            LastStatus = status;
            TriggerPropChange(nameof(LastStatus));
        }
        public void UpdateSyncedStatus(string sessionId, bool val)
        {
            lock(SyncedMap)
            {
                if (SyncedMap.ContainsKey(sessionId))
                    SyncedMap[sessionId] = val;
                else
                    SyncedMap.Add(sessionId, val);
            }
            TriggerPropChange(nameof(SyncedMap));
            TriggerPropChange(nameof(IsSynced));
        }
        public void UpdatePreparedStatus(bool val)
        {
            IsPrepared = val;
            TriggerPropChange(nameof(IsPrepared));
            //TriggerPropChange(nameof(IsSynced));
        }
        internal void SelectSessionID(string sessionID)
        {
            SelectedSessionID = sessionID;
            TriggerPropChange(nameof(SelectedSessionID));
            TriggerPropChange(nameof(IsSynced));
        }

        /// <summary>
        /// Trigger PropertyChanged for all provided property names
        /// </summary>
        private void TriggerPropChange(params string[] names)
        {
            foreach (string name in names)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
