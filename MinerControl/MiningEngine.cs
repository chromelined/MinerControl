﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using MinerControl.PriceEntries;
using MinerControl.Services;
using MinerControl.Utility;
using MinerControl.Utility.Multicast;

namespace MinerControl
{
    public class MiningEngine
    {
        private const int RemotePortNumber = 12814;

        private Process _process;
        private IList<AlgorithmEntry> _algorithmEntries = new List<AlgorithmEntry>();
        private IList<PriceEntryBase> _priceEntries = new List<PriceEntryBase>();
        private IList<IService> _services = new List<IService>();
        private decimal _powerCost;
        private decimal _exchange;
        private string _currencyCode;
        private string _currencySymbol;
        private bool _logactivity;
        private PriceEntryBase _currentRunning;
        private DateTime? _startMining;
        private TimeSpan _minTime;
        private TimeSpan _maxTime;
        private TimeSpan _switchTime;
        private TimeSpan _deadtime;
        private int? _nextRun;                    // Next algo to run
        private DateTime? _nextRunFromTime;       // When the next run algo became best

        public MiningModeEnum MiningMode { get; set; }

        public int MinerKillMode { get; private set; }
        public int GridSortMode { get; private set; }
        public int TrayMode { get; private set; }     // How to handle minimizing ot the tray

        public int? CurrentRunning { get { return _currentRunning == null ? (int?)null : _currentRunning.Id; } }
        public int? NextRun { get { return _nextRun; } }
        public DateTime? StartMining { get { return _startMining; } }
        public decimal PowerCost { get { return _powerCost; } }
        public decimal Exchange { get { return _exchange; } }
        public string CurrencyCode { get { return _currencyCode; } }
        public string CurrencySymbol { get { return _currencySymbol; } }
        public TimeSpan DeadTime { get { return _deadtime; } }

        public TimeSpan? RestartTime
        {
            get
            {
                return _startMining.HasValue && _maxTime > TimeSpan.Zero
                    ? _maxTime - (DateTime.Now - _startMining.Value)
                    : (TimeSpan?)null;
            }
        }

        public TimeSpan? MiningTime
        {
            get
            {
                return _startMining.HasValue
                    ? DateTime.Now - _startMining.Value
                    : (TimeSpan?)null;
            }
        }

        // How long until next run starts
        public TimeSpan? NextRunTime
        {
            get
            {
                if (_nextRun == null || _nextRunFromTime == null || _startMining == null)
                    return (TimeSpan?)null;

                var timeToSwitch = _switchTime - (DateTime.Now - _nextRunFromTime);
                var timeToMin = _minTime - (DateTime.Now - _startMining);

                return timeToMin > timeToSwitch ? timeToMin : timeToSwitch;
            }
        }

        public IList<IService> Services { get { return _services; } }
        public IList<PriceEntryBase> PriceEntries { get { return _priceEntries; } }       
        public IList<AlgorithmEntry> AlgorithmEntries { get { return _algorithmEntries; } }

        public TimeSpan TotalTime
        {
            get
            {
                var totalTime = PriceEntries.Sum(o => o.TimeMining.TotalMilliseconds);
                if (_startMining.HasValue)
                    totalTime += (DateTime.Now - _startMining.Value).TotalMilliseconds;
                return TimeSpan.FromMilliseconds(totalTime);
            }
        }

        // Signals for UI updates
        private volatile bool _pricesUpdated;
        private volatile bool _hasPrices;
        public bool PricesUpdated { get { return _pricesUpdated; } set { _pricesUpdated = value; } }
        public bool HasPrices { get { return _hasPrices; } set { _hasPrices = value; } }

        #region Donation mining settings

        private double _donationpercentage = 0.02;
        public bool DoDonationMinging { get { return _donationpercentage > 0 && _donationfrequency > TimeSpan.Zero; } }
        private TimeSpan _donationfrequency = TimeSpan.FromMinutes(240);
        private TimeSpan _autoMiningTime = TimeSpan.Zero;
        private TimeSpan _donationMiningTime = TimeSpan.Zero;
        private MiningModeEnum _donationMiningMode = MiningModeEnum.Stopped;

        private TimeSpan MiningBeforeDonation
        {
            get
            {
                if (!DoDonationMinging) return TimeSpan.Zero;
                return TimeSpan.FromMinutes(_donationfrequency.TotalMinutes * (1 - _donationpercentage));
            }
        }

        private TimeSpan MiningDuringDonation
        {
            get
            {
                if (!DoDonationMinging) return TimeSpan.Zero;
                return _donationfrequency - MiningBeforeDonation;
            }
        }

        public TimeSpan TimeUntilDonation
        {
            get
            {
                if (!DoDonationMinging) return TimeSpan.Zero;
                var miningTime = _autoMiningTime;
                if (MiningMode == MiningModeEnum.Automatic && _startMining.HasValue) miningTime += (DateTime.Now - _startMining.Value);
                var value = MiningBeforeDonation - miningTime;
                return value < TimeSpan.Zero ? TimeSpan.Zero : value;
            }
        }

        public TimeSpan TimeDuringDonation
        {
            get
            {
                if (!DoDonationMinging) return TimeSpan.Zero;
                var miningTime = _donationMiningTime;
                if (MiningMode == MiningModeEnum.Donation && _startMining.HasValue) miningTime += (DateTime.Now - _startMining.Value);
                var value = MiningDuringDonation - miningTime;
                return value < TimeSpan.Zero ? TimeSpan.Zero : value;
            }
        }

        #endregion

        #region Remote console

        private bool _remoteSend;
        private bool _remoteReceive;
        private IPEndPoint _endPoint = new IPEndPoint(new IPAddress(new byte[] { 239, 14, 10, 30 }), RemotePortNumber);
        private MulticastSender _remoteSender;
        private MulticastReceiver _remoteReceiver;

        public bool RemoteReceive { get { return _remoteReceive; } }

        #endregion

        public MiningEngine()
        {
            MinerKillMode = 1;
            GridSortMode = 1;
            MiningMode = MiningModeEnum.Stopped;
        }

        public void Cleanup()
        {
            WriteConsoleAction = null;

            if (_currentRunning != null && _currentRunning.UseWindow == false)
                StopMiner();

            if (_process != null)
                _process.Dispose();

            if (_remoteSender != null)
                _remoteSender.Dispose();

            if (_remoteReceiver != null)
                _remoteReceiver.Dispose();
        }

        public bool LoadConfig()
        {
            const string configFile = "MinerControl.conf";
            if (!File.Exists(configFile))
            {
                MessageBox.Show(string.Format("Config file, '{0}', not found.", configFile), "Miner Control: Config file missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            Dictionary<string, object> data;

            try
            {
                var pageString = File.ReadAllText(configFile);
                var serializer = new JavaScriptSerializer();
                data = serializer.DeserializeObject(pageString) as Dictionary<string, object>;
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(string.Format("Error loading config file: '{0}'.", ex.Message), "Miner Control: Config file error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            try
            {
                LoadConfigGeneral(data["general"] as Dictionary<string, object>);
                LoadConfigAlgorithms(data["algorithms"] as object[]);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Error processing general configuration: '{0}'.", ex.Message), "Miner Control: Config file error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            try
            {
                LoadService(new NiceHashService(), data, "nicehash");
                LoadService(new WestHashService(), data, "westhash");
                LoadService(new TradeMyBitService(), data, "trademybit");
                LoadService(new YaampService(), data, "yaamp");
                LoadService(new WafflePoolService(), data, "wafflepool");
                LoadService(new LtcRabbitService(), data, "ltcrabbit");
                LoadService(new WePayBtcService(), data, "wepaybtc");
                LoadService(new HamsterPoolService(), data, "hamsterpool");
                LoadService(new ManualService(), data, "manual");

                // Set Id for each entry
                for (var x = 0; x < _priceEntries.Count; x++)
                    _priceEntries[x].Id = x + 1;
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Error processing service configuration: '{0}'.", ex.Message), "Miner Control: Configuration file error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (_remoteSend)
            {
                _remoteSender = new MulticastSender(_endPoint, 1);
                _remoteSender.Start();
            }

            if (_remoteReceive)
            {
                _remoteReceiver = new MulticastReceiver(_endPoint);
                _remoteReceiver.DataReceived += ProcessRemoteData;
                _remoteReceiver.Start();
            }

            return true;
        }

        private void LoadService(IService service, IDictionary<string, object> data, string name) 
        {
            if (!data.ContainsKey(name)) return;
            service.MiningEngine = this;
            _services.Add(service);
            service.Initialize(data[name] as Dictionary<string, object>);
        }

        private void LoadConfigGeneral(IDictionary<string, object> data)
        {
            _powerCost = data["power"].ExtractDecimal();
            _exchange = data["exchange"].ExtractDecimal();
            if (data.ContainsKey("currencycode"))
                _currencyCode = data["currencycode"].ToString().ToUpper();
            _minTime = TimeSpan.FromMinutes((double)data["mintime"].ExtractDecimal());
            _maxTime = TimeSpan.FromMinutes((double)data["maxtime"].ExtractDecimal());
            _switchTime = TimeSpan.FromMinutes((double)data["switchtime"].ExtractDecimal());
            _deadtime = TimeSpan.FromMinutes((double)data["deadtime"].ExtractDecimal());

            if (data.ContainsKey("logerrors"))
                ErrorLogger.LogExceptions = bool.Parse(data["logerrors"].ToString());
            if (data.ContainsKey("logactivity"))
                _logactivity = bool.Parse(data["logactivity"].ToString());
            if (data.ContainsKey("minerkillmode"))
                MinerKillMode = int.Parse(data["minerkillmode"].ToString());
            if (data.ContainsKey("gridsortmode"))
                GridSortMode = int.Parse(data["gridsortmode"].ToString());
            if (data.ContainsKey("traymode"))
                TrayMode = int.Parse(data["traymode"].ToString());
            if (Program.MinimizeToTray && TrayMode == 0)
                TrayMode = 2;
            if (data.ContainsKey("donationpercentage"))
                _donationpercentage = (double)(data["donationpercentage"].ExtractDecimal()) / 100;
            if (data.ContainsKey("donationfrequency"))
                _donationfrequency = TimeSpan.FromMinutes((double)data["donationfrequency"].ExtractDecimal());
            if (data.ContainsKey("remotesend"))
                _remoteSend = bool.Parse(data["remotesend"].ToString());
            if (data.ContainsKey("remotereceive"))
                _remoteReceive = bool.Parse(data["remotereceive"].ToString());
        }

        private void LoadConfigAlgorithms(object[] data)
        {
            foreach (var rawitem in data)
            {
                var item = rawitem as Dictionary<string, object>;
                var entry = new AlgorithmEntry
                {
                    Name = item["name"] as string,
                    Display = item.ContainsKey("display") ? item["display"] as string : GetAlgoDisplayName(item["name"] as string),
                    Hashrate = item["hashrate"].ExtractDecimal(),
                    Power = item["power"].ExtractDecimal(),
                    Param1 = item.GetString("aparam1") ?? string.Empty,
                    Param2 = item.GetString("aparam2") as string ?? string.Empty,
                    Param3 = item.GetString("aparam3") as string ?? string.Empty
                };

                _algorithmEntries.Add(entry);
            }
        }

        public void StopMiner()
        {
            if (!_process.IsRunning())
            {
                _process = null;
                return;
            }

            LogActivity(_donationMiningMode == MiningModeEnum.Donation ? "DonationStop" : "Stop");
            WriteConsole(string.Format("Stopping {0} {1}", _currentRunning.ServicePrint, _currentRunning.AlgoName), true);
            RecordMiningTime();
            if (MinerKillMode == 0)
                ProcessUtil.KillProcess(_process);
            else
                ProcessUtil.KillProcessAndChildren(_process.Id);

            _process = null;
            _donationMiningMode = MiningModeEnum.Stopped;

            if (_currentRunning != null)
            {
                var entry = PriceEntries.Where(o => o.Id == _currentRunning.Id).Single();
                entry.UpdateStatus();
            }

            _currentRunning = null;
        }

        private void RecordMiningTime()
        {
            if (_currentRunning == null || !_startMining.HasValue) return;

            if (_donationMiningMode == MiningModeEnum.Automatic) _autoMiningTime += (DateTime.Now - _startMining.Value);
            if (_donationMiningMode == MiningModeEnum.Donation) _donationMiningTime += (DateTime.Now - _startMining.Value);

            _currentRunning.TimeMining += (DateTime.Now - _startMining.Value);
            _currentRunning.UpdateStatus();
            _startMining = null;
        }

        private void StartMiner(PriceEntryBase entry, bool isMinimizedToTray = false)
        {
            _nextRun = null;
            _nextRunFromTime = null;
            _currentRunning = entry;
            _startMining = DateTime.Now;

            _process = new Process();
            if (_donationMiningMode == MiningModeEnum.Donation)
            {
                if (!string.IsNullOrWhiteSpace(entry.DonationFolder))
                    _process.StartInfo.WorkingDirectory = entry.DonationFolder;
                _process.StartInfo.FileName = string.IsNullOrWhiteSpace(entry.DonationFolder)
                    ? entry.DonationCommand
                    : string.Format(@"{0}\{1}", entry.DonationFolder, entry.DonationCommand);
                _process.StartInfo.Arguments = entry.DonationArguments;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(entry.Folder))
                    _process.StartInfo.WorkingDirectory = entry.Folder;
                _process.StartInfo.FileName = string.IsNullOrWhiteSpace(entry.Folder)
                    ? entry.Command
                    : string.Format(@"{0}\{1}", entry.Folder, entry.Command);
                _process.StartInfo.Arguments = entry.Arguments;
            }

            WriteConsole(string.Format("Starting {0} {1} with {2} {3}", _currentRunning.ServicePrint, _currentRunning.Name, _process.StartInfo.FileName, _process.StartInfo.Arguments), true);

            if (!string.IsNullOrWhiteSpace(_process.StartInfo.WorkingDirectory) && !Directory.Exists(_process.StartInfo.WorkingDirectory))
            {
                entry.DeadTime = DateTime.Now;
                var message = string.Format("Path '{0}' does not exist.", _process.StartInfo.WorkingDirectory);
                _process = null;
                WriteConsole(message, true);
                throw new ArgumentException(message);
            }
            if (!string.IsNullOrWhiteSpace(_process.StartInfo.FileName) && !File.Exists(_process.StartInfo.FileName))
            {
                entry.DeadTime = DateTime.Now;
                var message = string.Format("File '{0}' does not exist.", _process.StartInfo.FileName);
                _process = null;
                WriteConsole(message, true);
                throw new ArgumentException(message);
            }

            if (entry.UseWindow)
            {
                _process.StartInfo.WindowStyle = (isMinimizedToTray && TrayMode == 2) ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Minimized;
                _process.Start();

                Thread.Sleep(100);
                try
                {
                    ProcessUtil.SetWindowTitle(_process, string.Format("{0} {1} Miner", entry.ServicePrint, entry.Name));
                }
                catch (Exception ex)
                {
                    ErrorLogger.Log(ex);
                }

                if (isMinimizedToTray && TrayMode == 1)
                    HideMinerWindow();
            }
            else
            {
                _process.StartInfo.RedirectStandardOutput = true;
                _process.StartInfo.RedirectStandardError = true;
                _process.EnableRaisingEvents = true;
                _process.StartInfo.CreateNoWindow = true;
                _process.StartInfo.UseShellExecute = false;

                _process.ErrorDataReceived += ProcessConsoleOutput;
                _process.OutputDataReceived += ProcessConsoleOutput;
                
                _process.Start();

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }

            _startMining = DateTime.Now;
            _donationMiningMode = MiningMode;

            entry.UpdateStatus();

            LogActivity(_donationMiningMode == MiningModeEnum.Donation ? "DonationStart" : "Start");
        }

        private void ClearDeadTimes()
        {
            foreach (var entry in _priceEntries)
                entry.DeadTime = DateTime.MinValue;
        }

        public void RequestStop()
        {
            _nextRun = null;
            _nextRunFromTime = null;

            StopMiner();
            ClearDeadTimes();
        }

        public void RequestStart(int id, bool isMinimizedToTray = false)
        {
            var entry = _priceEntries.Single(o => o.Id == id);
            StartMiner(entry, isMinimizedToTray);
        }

        public void CheckPrices()
        {
            foreach (var service in _services)
                service.CheckPrices();
        }

        public void RunBestAlgo(bool isMinimizedToTray)
        {
            try
            {
                // Check for dead process
                if (!_process.IsRunning() && _currentRunning != null)
                {
                    lock (this)
                    {
                        _currentRunning.DeadTime = DateTime.Now;
                        LogActivity(_donationMiningMode == MiningModeEnum.Donation ? "DonationDead" : "Dead");
                        WriteConsole(string.Format("Dead {0} {1}", _currentRunning.ServicePrint, _currentRunning.Name), true);
                        RecordMiningTime();
                    }
                }

                // Clear information if process not running
                if (_process == null || _process.HasExited)
                {
                    _currentRunning = null;
                    _startMining = null;
                    _nextRun = null;
                    _nextRunFromTime = null;
                }

                // Donation mining
                if (DoDonationMinging)
                {
                    if (_donationMiningMode == MiningModeEnum.Automatic && TimeUntilDonation == TimeSpan.Zero)
                    {
                        StopMiner();
                        _donationMiningMode = MiningModeEnum.Donation;
                        MiningMode = _donationMiningMode;
                        _autoMiningTime = TimeSpan.Zero;
                    }
                    else if (_donationMiningMode == MiningModeEnum.Donation && TimeDuringDonation == TimeSpan.Zero)
                    {
                        StopMiner();
                        _donationMiningMode = MiningModeEnum.Automatic;
                        MiningMode = _donationMiningMode;
                        _donationMiningTime = TimeSpan.Zero;
                    }
                }

                // Restart miner if max time reached
                if (RestartTime.HasValue && RestartTime.Value <= TimeSpan.Zero)
                    StopMiner();

                // Find the best, live entry
                var best = _donationMiningMode == MiningModeEnum.Donation
                    ? _priceEntries
                        .Where(o => !o.IsDead)
                        .Where(o => !string.IsNullOrWhiteSpace(o.DonationCommand))
                        .OrderByDescending(o => o.NetEarn)
                        .First()
                    : _priceEntries
                        .Where(o => !o.IsDead)
                        .Where(o => !string.IsNullOrWhiteSpace(o.Command))
                        .OrderByDescending(o => o.NetEarn)
                        .First();

                // Handle minimum time for better algorithm before switching
                if (_switchTime > TimeSpan.Zero && _currentRunning != null)
                {
                    if (!_nextRun.HasValue && _currentRunning.Id != best.Id)
                    {
                        _nextRun = best.Id;
                        _nextRunFromTime = DateTime.Now;
                    }
                    else if (_nextRun.HasValue && _currentRunning.Id == best.Id)
                    {
                        _nextRun = null;
                        _nextRunFromTime = null;
                    }
                    if (NextRunTime.HasValue && NextRunTime > TimeSpan.Zero)
                        best = _priceEntries.First(o => o.Id == _currentRunning.Id);
                }

                // Update undead entries
                var entries = PriceEntries.Where(o => !o.IsDead && o.DeadTime != DateTime.MinValue);
                foreach (var entry in entries)
                    entry.DeadTime = DateTime.MinValue;

                // Just update time if we are already running the right entry
                if (_currentRunning != null && _currentRunning.Id == best.Id)
                {
                    _currentRunning.UpdateStatus();
                    return;
                }

                // Honor minimum time to run in auto mode
                if (MiningTime.HasValue && MiningTime.Value < _minTime)   
                {
                    _currentRunning.UpdateStatus();
                    return;
                }

                StopMiner();
                StartMiner(best, isMinimizedToTray);
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ex);
            }
        }

        public void HideMinerWindow()
        {
            if (_currentRunning == null || !_currentRunning.UseWindow) return;

            ProcessUtil.HideWindow(_process);
        }

        public void MinimizeMinerWindow()
        {
            if (_currentRunning == null || !_currentRunning.UseWindow) return;

            ProcessUtil.MinimizeWindow(_process);
        }

        public void LoadExchangeRates()
        {
            WebUtil.DownloadJson("http://blockchain.info/ticker", ProcessExchangeRates);
        }

        private void ProcessExchangeRates(object jsonData)
        {
            var data = jsonData as Dictionary<string, object>;
            if (!data.ContainsKey(_currencyCode)) return;
            var item = data[_currencyCode] as Dictionary<string, object>;
            var exchange = item["last"].ExtractDecimal();
            var symbol = item["symbol"].ToString();
            lock (this)
            {
                if (exchange > 0 && exchange != _exchange)
                {
                    _exchange = exchange;
                    if (PriceEntries != null)
                        foreach (var entry in PriceEntries)
                            entry.UpdateExchange();
                    if (Services != null)
                        foreach (var service in Services)
                            service.UpdateExchange();
                }

                if (!string.IsNullOrWhiteSpace(symbol)) _currencySymbol = symbol;
            }
        }

        private void LogActivity(string action)
        {
            if (!_logactivity) return;

            var items = new[]
            {
                DateTime.Now.ToString("s"),
                action,
                MiningMode.ToString(),
                _currentRunning != null ? _currentRunning.ServicePrint : string.Empty,
                _currentRunning != null ? _currentRunning.AlgoName : string.Empty,
                _currentRunning != null ? _currentRunning.Price.ToString("F6") : string.Empty,
                _currentRunning != null ? _currentRunning.Earn.ToString("F6") : string.Empty,
                _currentRunning != null ? _currentRunning.Fees.ToString("F6") : string.Empty,
                _currentRunning != null ? _currentRunning.Power.ToString("F6") : string.Empty,
                _currentRunning != null ? _currentRunning.NetEarn.ToString("F6") : string.Empty,
                _exchange.ToString("F2"),
                _currentRunning != null ? _currentRunning.ServiceEntry.Balance.ToString("F8") : string.Empty
            };

            var line = string.Join(",", items);
            const string logfile = "activity.log";

            var exists = File.Exists(logfile);
            using (var w = exists ? File.AppendText(logfile) : File.CreateText(logfile))
            {
                if (!exists)
                    w.WriteLine("time,action,mode,service,algo,price,earn,fees,power,netearn,exchange,servicebalance");
                w.WriteLine(line);
            }
        }

        #region Console interaction

        public Action<string> WriteConsoleAction { get; set; }

        private void WriteConsole(string text, bool prefixTime = false)
        {
            if (WriteConsoleAction == null) return;

            if (prefixTime)
                text = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, text);

            WriteConsoleAction(text);

            if (_remoteSender == null) return;
            text = string.Format("{0} {1}", "CON", text);
            _remoteSender.Send(text);
        }

        private void ProcessConsoleOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            WriteConsole(e.Data);
        }

        public Action<IPAddress, string> WriteRemoteAction { get; set; }

        private void ProcessRemoteData(object sender, MulticastDataReceivedEventArgs e)
        {
            if (WriteRemoteAction == null) return;

            try
            {
                var data = e.StringData;
                var command = data.Substring(0, 4);
                var body = data.Substring(4);

                switch (command)
                {
                    case "CON ":
                        WriteRemoteAction(e.RemoteEndPoint.Address, body);
                        break;
                }
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ex);
            }
        }

        #endregion

        #region Helpers

        private readonly IDictionary<string, string> _algoNames = new Dictionary<string, string>
        {
            {"x11", "X11"},
            {"x13", "X13"},
            {"x14", "X14"},
            {"x15", "X15"},
            {"scrypt", "Scrypt"},
            {"scryptn", "Scrypt-N"},
            {"sha256", "SHA256"},
            {"nist5", "Nist5"},
            {"keccak", "Keccak"},
            {"quark", "Quark"},
            {"neoscrypt", "NeoScrypt"}
        };

        private string GetAlgoDisplayName(string rawname)
        {
            if (_algoNames.ContainsKey(rawname))
                return _algoNames[rawname];
            return rawname;
        }

        #endregion
    }
}
