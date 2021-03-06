﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Diagnostics;


namespace CableGuardian
{
    public enum VRAPI { OculusVR, OpenVR }

    public partial class FormMain : Form
    {
        ToolTip TTip = new ToolTip() { AutoPopDelay = 30000 };
        ContextMenuStrip TrayMenu = new ContextMenuStrip();
        ToolStripLabel TrayMenuTitle = new ToolStripLabel(Config.ProgramTitle);
        ToolStripLabel TrayMenuRotations = new ToolStripLabel("Half turns: 00000000");
        ToolStripMenuItem TrayMenuReset = new ToolStripMenuItem("Reset turn counter");
        ToolStripSeparator TrayMenuSeparator1 = new ToolStripSeparator();
        ToolStripMenuItem TrayMenuAlarmIn = new ToolStripMenuItem("Alarm me in");
        ToolStripMenuItem TrayMenuAlarmAt = new ToolStripMenuItem("Alarm me at");
        ToolStripMenuItem TrayMenuAlarmClear = new ToolStripMenuItem("Cancel alarm");
        ToolStripSeparator TrayMenuSeparator2 = new ToolStripSeparator();
        ToolStripMenuItem TrayMenuGUI = new ToolStripMenuItem("Restore from tray");
        ToolStripMenuItem TrayMenuExit = new ToolStripMenuItem("Quit");
        public static bool RunFromDesigner { get { return (LicenseManager.UsageMode == LicenseUsageMode.Designtime); } }

        internal static YawTracker Tracker { get; private set; }
        internal static OculusConnection OculusConn { get; private set; } = new OculusConnection();
        internal static OpenVRConnection OpenVRConn { get; private set; } = new OpenVRConnection();
        internal static AudioDevicePool WaveOutPool { get; private set; } = new AudioDevicePool(OculusConn);

        VRObserver Observer;
        internal static VRConnection ActiveConnection { get; private set; }
        Timer AlarmTimer = new Timer();        
        DateTime AlarmTime;
        int TimerHours = 0;
        int TimerMinutes = 0;
        int TimerSeconds = 0;

        Point MouseDragPosOnForm = new Point(0, 0);        
        bool UpdateYawToForm = false;
        bool SkipFlaggedEventHandlers = false;
        bool ProfilesSaved = false;
        bool MouseDownOnComboBox = false;
        public static bool IntentionalAPIChange = false;
        /// <summary>
        /// One-time flag to allow hiding the form at startup
        /// </summary>
        bool ForceHide = true;
        bool IsRestart = false;
        string RestartArgs = "";        


        public FormMain()
        {   
            InitializeComponent();

            ExitIfAlreadyRunning();                

            // poll interval of 180ms should suffice (5.5 Hz) ...// UPDATE: Tightened to 150ms (6.67 Hz) just to be on the safe side. Still not too much CPU usage.
            // (head rotation must stay below 180 degrees between samples)
            Observer = new VRObserver(OculusConn, 150);
            Observer.Start();
                                    
            if (!RunFromDesigner)
            {
                InitializeTrayMenu();
                InitializeAppearance();
            }
            
            AddEventHandlers();

            ReadConfigFromFileAndCheckDefaultSounds();
            Tracker = new YawTracker(Observer, GetInitialHalfTurn(), Config.LastYawValue); // after reading config but before reading profiles

            ReadProfilesFromFile();
            LoadConfigToGui();
            LoadStartupProfile();

            if (Config.ConfigFileMissingAtStartup || Config.IsLegacyConfig)            
                UpdateMissingOrLegacyConfig();

            SaveConfigurationToFile(); // always save config at startup to reset last exit

            if (Config.ProfilesFileMissingAtStartup || Config.IsLegacyConfig)
                SaveProfilesToFile();

            SetProfilesSaveStatus(true);
            
            if (Config.ConfigFileMissingAtStartup) // Most likely a first time launch
            {                
                string msg = $"Welcome to {Config.ProgramTitle}!{Environment.NewLine}{Environment.NewLine}" +
                        $"1. For help on a setting, hover the mouse over it.   {Environment.NewLine}{Environment.NewLine}" +
                        $"2. For an overview, click the \"?\" in the top right corner.{Environment.NewLine}{Environment.NewLine}" +
                        $"3. For a quick menu, right click the CG icon in the system tray.";
                MessageBox.Show(this, msg, "First time launch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ShowTemporaryTrayNotification(2000, "Welcome to " + Config.ProgramTitle + "!", "Check out the CG icon in the system tray. ");
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(ForceHide ? false : value);
        }

        void ExitIfAlreadyRunning()
        {
            // Exit if already running from the same location. Multiple instances are allowed from different locations for no particular reason.

            // Rather than showing a notification from this new instance...
            // ...it might be cleaner to use a mutex and send a message to the existing instance without ever starting forms but... nah            

            try
            {
                string cgName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
                Process current = Process.GetCurrentProcess();
                if (Process.GetProcessesByName(cgName).Where
                    (
                        p => p.Id != current.Id
                        &&
                        String.Compare(p.MainModule.FileName, current.MainModule.FileName, true) == 0 // only from the same location
                    ).Any())
                {
                    notifyIcon1.Icon = CableGuardian.Properties.Resources.CG_error;
                    if (!Program.IsAutoStartup) // show notification only on user startup
                    {
                        ShowTemporaryTrayNotification(3300, "", $"{Config.ProgramTitle} is already running.");
                        System.Threading.Thread.Sleep(3300);
                    }
                    notifyIcon1.Visible = false; // otherwise the empty icon lingers in the tray
                    Environment.Exit(0);
                }
            }
            catch (Exception e)
            {
                Config.WriteLog("Failed to check existing instance. " + e.Message);
            }
        }

        int GetInitialHalfTurn()
        {
            // first from command line (overrides config)
            int initialHalfTurn = 0;
            Regex rx = new Regex(@"-?\d");
            MatchCollection matches = rx.Matches(Program.CmdArgsLCase);
            if (matches.Count > 0)
            {
                int.TryParse(matches[0].Value, out initialHalfTurn);
                return initialHalfTurn;
            }

            // then config:
            return Config.GetInitialHalfTurn();
        }

       
        void UpdateMissingOrLegacyConfig()
        {
            try
            {
                // rewrite registry for win startup if existing:
                if (Config.ReadWindowsStartupFromRegistry())
                    Config.WriteWindowsStartupToRegistry(true);
            }
            catch (Exception)
            {
                // intentionally ignore
            }

            // another hacky solution... 
            if (ActiveConnection == OculusConn)
            {
                Config.NotifyOnAPIQuit = true; // this is better to be on by default for Oculus but not for OpenVR ... imo
                SkipFlaggedEventHandlers = true;
                checkBoxOnAPIQuit.Checked = true;
                SkipFlaggedEventHandlers = false;
            }
                        
        }
               
        void ReadConfigFromFileAndCheckDefaultSounds()
        {
            try
            {
                Config.ReadConfigFromFile();
            }
            catch (Exception e)
            {
                Config.WriteLog($"Error when reading configuration ({Program.ConfigFile})." + Environment.NewLine + e.Message);                
            }

            try
            {
                Config.CheckDefaultSounds();
            }
            catch (Exception ex)
            {
                string msg = String.Format("Unable* to load default sounds.  {0}{0} * {1}", Environment.NewLine, ex.Message);
                Config.WriteLog(msg);
                RestoreFromTray();
                MessageBox.Show(this, msg, Config.ProgramTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

       
        void ReadProfilesFromFile()
        {
            try
            {
                Config.ReadProfilesFromFile();
            }
            catch (Exception ex)
            {
                string msg = String.Format("Unable* to load profiles from file. {2} will not be sounding any alerts until a new profile has been defined.  {0}{0} * {1}", Environment.NewLine, ex.Message, Config.ProgramTitle);
                RestoreFromTray();
                MessageBox.Show(this, msg, Config.ProgramTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
              
        void LoadConfigToGui()
        {
            SkipFlaggedEventHandlers = true;            
            checkBoxStartMinUser.Checked = Config.MinimizeAtUserStartup;
            checkBoxStartMinAuto.Checked = Config.MinimizeAtAutoStartup;
            checkBoxConnLost.Checked = Config.NotifyWhenVRConnectionLost;
            checkBoxSticky.Checked = Config.ConnLostNotificationIsSticky;
            checkBoxOnAPIQuit.Checked = Config.NotifyOnAPIQuit;
            checkBoxTrayNotifications.Checked = Config.TrayMenuNotifications;
            checkBoxPlaySoundOnHMDInteraction.Checked = Config.PlaySoundOnHMDinteractionStart;
            checkBoxRememberRotation.Checked = Config.TurnCountMemoryMinutes > -1;
            numericUpDownRotMemory.Value = (Config.TurnCountMemoryMinutes > -1) ? Config.TurnCountMemoryMinutes : 0 ;
            SkipFlaggedEventHandlers = false;

            if (Program.CmdArgsLCase.Contains(Program.Arg_Maximized))
            {
                RestoreFromTray();
            }
            else if (Program.CmdArgsLCase.Contains(Program.Arg_Minimized) == false)
            {
                if (!Config.MinimizeAtUserStartup && !Program.IsAutoStartup)
                    RestoreFromTray();

                if (!Config.MinimizeAtAutoStartup && Program.IsAutoStartup)
                    RestoreFromTray();
            }

            CheckWindowsStartUpStatus();
            SetControlVisibility();
        }

        void SetControlVisibility()
        {
            if (checkBoxPlaySoundOnHMDInteraction.Checked)
            {
                checkBoxPlaySoundOnHMDInteraction.Text = "Play confirmation sound   --->";
                buttonJingle.Visible = true;
            }
            else
            {
                checkBoxPlaySoundOnHMDInteraction.Text = "Play confirmation sound";
                buttonJingle.Visible = false;
            }

            if (checkBoxRememberRotation.Checked)
            {
                checkBoxRememberRotation.Text = "Remember turn count for   ---> ";
                numericUpDownRotMemory.Visible = true;
                labelRotMemMinutes.Visible = true;

                if (numericUpDownRotMemory.Value == 0)
                {
                    labelRotMemMinutes.Text = "ever";
                    labelRotMemMinutes.Location = new Point(numericUpDownRotMemory.Location.X + 17, numericUpDownRotMemory.Location.Y + 2);
                }
                else
                {
                    labelRotMemMinutes.Text = "minutes";
                    labelRotMemMinutes.Location = new Point(numericUpDownRotMemory.Location.X + numericUpDownRotMemory.Width + 10, numericUpDownRotMemory.Location.Y + 2);
                }

            }
            else
            {
                checkBoxRememberRotation.Text = "Remember turn count";
                numericUpDownRotMemory.Visible = false;
                labelRotMemMinutes.Visible = false;
            }
        }

        void LoadStartupProfile()
        {
            RefreshProfileCombo();
            Profile lastSessionProfile = Config.Profiles.Where(p => p.Name == Config.LastSessionProfileName).FirstOrDefault();
            Profile startProf = (Config.StartUpProfile ?? lastSessionProfile) ?? Config.Profiles.FirstOrDefault();

            SkipFlaggedEventHandlers = true;
            comboBoxProfile.SelectedItem = startProf;                        
            SkipFlaggedEventHandlers = false;

            LoadProfile(startProf);
        }

        void InitializeTrayMenu()
        {
            TrayMenuTitle.Font = new Font(TrayMenuTitle.Font, FontStyle.Bold);
            TrayMenuTitle.ForeColor = Config.CGErrorColor;
            TrayMenuRotations.Font = new Font(TrayMenuRotations.Font, FontStyle.Bold);

            notifyIcon1.ContextMenuStrip = TrayMenu;
            TrayMenu.Items.Add(TrayMenuTitle);
            TrayMenu.Items.Add(TrayMenuRotations);            
            TrayMenu.Items.Add(TrayMenuReset);            
            TrayMenu.Items.Add(TrayMenuSeparator1);
            TrayMenu.Items.Add(TrayMenuAlarmIn);
            TrayMenu.Items.Add(TrayMenuAlarmAt);
            TrayMenu.Items.Add(TrayMenuAlarmClear);            
            TrayMenu.Items.Add(TrayMenuSeparator2);
            TrayMenu.Items.Add(TrayMenuGUI);
            TrayMenu.Items.Add(TrayMenuExit);

            BuilAlarmMenu();
        }

        void BuilAlarmMenu()
        {   

            for (int i = 0; i < 12; i++)
            {
                ToolStripMenuItem itemH = new ToolStripMenuItem(i.ToString() + "h");                
                itemH.Tag = i;
                TrayMenuAlarmIn.DropDownItems.Add(itemH);

                int ath = (i == 0) ? 12 : i;  // to get 12 first since it actually represents zero in the AM/PM system

                ToolStripMenuItem itemAMH = new ToolStripMenuItem(ath.ToString());
                itemAMH.Tag = (ath == 12) ? 0 : ath;
                TrayMenuAlarmAt.DropDownItems.Add(itemAMH);

                for (int j = 0; j < 60; j += 5)
                {
                    ToolStripMenuItem itemM = new ToolStripMenuItem(i.ToString() + "h " + j.ToString() + "min");
                    itemM.Tag = j;
                    itemH.DropDownItems.Add(itemM);
                    itemM.Click += TrayMenuAlarmInItem_Click;
                                       
                    ToolStripMenuItem itemAMM = new ToolStripMenuItem(ath.ToString() + ":" + ((j < 10) ? "0" : "") + j.ToString()); // + " AM");
                    itemAMM.Tag = j;
                    itemAMH.DropDownItems.Add(itemAMM);
                    itemAMM.Click += TrayMenuAlarmAtItem_Click;
                }
            }
        }        

               
        void InitializeAppearance()
        {
            InitializeAppearanceCommon(this);

            StartPosition = FormStartPosition.CenterScreen;
            notifyIcon1.Text = Config.ProgramTitle;
            notifyIcon1.Icon = CableGuardian.Properties.Resources.CG_error;            
            Icon = CableGuardian.Properties.Resources.CG_error;
            TTip.SetToolTip(pictureBoxMinimize, "Minimize to tray");
            TTip.SetToolTip(pictureBoxPlus, "Add a new empty profile");
            TTip.SetToolTip(pictureBoxClone, "Clone the current profile");
            TTip.SetToolTip(pictureBoxMinus, "Delete the current profile");            
            TTip.SetToolTip(checkBoxConnLost, $"Show a Windows notification and play a sound when connection to the VR headset unexpectedly changes from OK to NOT OK.");
            TTip.SetToolTip(checkBoxOnAPIQuit, $"Show connection lost notification when the VR API requests {Config.ProgramTitle} to quit.{Environment.NewLine}" +
                                                $"Most common examples are when closing SteamVR or restarting Oculus.");
            TTip.SetToolTip(checkBoxSticky, $"If checked, the connection lost notification stays in the Windows notification list until cleared.{Environment.NewLine}" +
                                            "Otherwise the notification disappears automatically after a few seconds.");
            TTip.SetToolTip(buttonReset, $"Reset turn counter to zero. Use this if your cable twisting is not in sync with the app. Cable should be straight when counter = 0." + Environment.NewLine
                                        + $"NOTE that the reset can also be done from the {Config.ProgramTitle} tray icon.");
            TTip.SetToolTip(buttonAlarm, $"Adjust the alarm clock sound. Use the {Config.ProgramTitle} tray icon to set the alarm.");
            TTip.SetToolTip(checkBoxTrayNotifications, $"When checked, a Windows notification is displayed when you make selections in the {Config.ProgramTitle} tray menu. (for feedback)");
            TTip.SetToolTip(checkBoxShowYaw, $"Show rotation data to confirm functionality. Keep it hidden to save a few of those precious CPU cycles.{Environment.NewLine}" 
                                            + $"Some headsets / API versions might require that the headset is on your head for tracking to work.");
            TTip.SetToolTip(checkBoxPlaySoundOnHMDInteraction, $"Play a sound when putting on the VR headset. Check this if you want to be sure that {Config.ProgramTitle} is up and running when starting a VR session." + Environment.NewLine + Environment.NewLine
                + "NOTES for OpenVR users:" + Environment.NewLine
                + "- Purely checking the proximity sensor through OpenVR API seemed impossible. Their implementation of \"User Interaction\" is based on both: movement and the prox sensor." + Environment.NewLine
                + "      \u2022 User interaction starts   a) when SteamVR is opened   b) when the proximity sensor is covered (after a stop). " + Environment.NewLine
                + "      \u2022 User interaction stops when the proximity sensor is uncovered but ONLY after the headset has been completely stationary (e.g. on a table) for 10 seconds." + Environment.NewLine + Environment.NewLine
                + "UPDATE Nov 2019: Apparently the behaviour was changed in SteamVR version 1.8 to prefer the proximity sensor if available.");
            TTip.SetToolTip(buttonJingle, $"Adjust the sound that plays when you put on the headset.");
            TTip.SetToolTip(comboBoxProfile, $"Switch between profiles. Only one profile can be active at a time.");
            TTip.SetToolTip(labelAutoStart, $"After dialing in your rotation settings, it's recommended to set an automatic startup for {Config.ProgramTitle}." + Environment.NewLine
                                            + "Note that SteamVR autostart toggle is available only after you have established a headset connection via OpenVR API." + Environment.NewLine + Environment.NewLine
                                            + "p.s. I also recommend trying the \"Play confirmation sound\" -feature that let's you know that the app is alive and well when you enter VR.");
            TTip.SetToolTip(checkBoxWindowsStart, $"Start {Config.ProgramTitle} automatically when Windows boots up. " + Environment.NewLine  
                                                + $"Note that {Config.ProgramTitle} will wait for {Program.WindowsStartupWaitInSeconds} seconds after boot before being available." + Environment.NewLine
                                                +"This is to ensure that all audio devices have been initialized by the OS before trying to use them.");
            TTip.SetToolTip(checkBoxSteamVRStart, $"Start and exit {Config.ProgramTitle} automatically with SteamVR." + Environment.NewLine + Environment.NewLine
                                                + "NOTE: You can toggle this only when an OpenVR connection has been established." + Environment.NewLine
                                                + $"NOTE2: {Config.ProgramTitle} will exit automatically only if it was automatically started by SteamVR. (not if user started the app)" + Environment.NewLine
                                                + $"NOTE3: Automatic start will be cancelled if an instance of {Config.ProgramTitle} is already running from the same location.");
            TTip.SetToolTip(checkBoxStartMinAuto, $"Hide GUI ( = tray icon only) when the program starts automatically with Windows. Recommended for normal usage after you have dialed in your settings.");
            TTip.SetToolTip(checkBoxStartMinUser, $"Hide GUI ( = tray icon only) when the user starts the program manually.");
            TTip.SetToolTip(checkBoxRememberRotation, $"Remember the turn count when {Config.ProgramTitle} is closed. Otherwise turn count is always zero at startup." + Environment.NewLine
                                                    + "You may find this convenient when using the SteamVR auto start & exit feature (OpenVR only).");
            TTip.SetToolTip(numericUpDownRotMemory, $"Time limit (minutes) for the turn count memory (when {Config.ProgramTitle} is closed). The last turn count will be used at startup if the elapsed time since the last exit is less or equal to this value." + Environment.NewLine
                                                    + "If more time has passed, turn count will be zero at startup. Useful when you want to make sure the turn count will be zero after a longer pause (during which you probably unwinded the cable)." + Environment.NewLine + Environment.NewLine
                                                    + "***    0 = no limit = remember forever    ***");

            buttonSave.ForeColor = Config.CGColor;            
            labelProf.ForeColor = Config.CGColor;
            labelYaw.ForeColor = Config.CGErrorColor;                        
            labelHalfTurns.ForeColor = Config.CGErrorColor;
            labelHalfTurnTitle.ForeColor = Config.CGErrorColor;
        }

        void InitializeAppearanceCommon(Control ctl)
        {
            if (ctl.Tag?.ToString() == "MANUAL") // skip tagged objects
                return;

            if (ctl is UserControl || ctl is Panel)
            {
                ctl.BackColor = Config.CGBackColor;
            }

            if (ctl is Label || ctl is CheckBox || ctl is Button)
            {
                ctl.ForeColor = Color.White;
            }

            foreach (Control item in ctl.Controls)
            {
                InitializeAppearanceCommon(item);
            }
        }

        void AddEventHandlers()
        {
            AddEventHandlersCommon(this);
            AddDragEventHandlers();

            FormClosing += FormMain_FormClosing;
            FormClosed += FormMain_FormClosed;
            notifyIcon1.MouseDoubleClick += NotifyIcon1_MouseDoubleClick;

            pictureBoxMinimize.MouseClick += PictureBoxMinimize_MouseClick;
            pictureBoxClose.MouseClick += PictureBoxClose_MouseClick;            
            pictureBoxMinus.Click += PictureBoxMinus_Click;
            pictureBoxPlus.Click += (s, e) => { AddProfile(); SetProfilesSaveStatus(false); };
            pictureBoxClone.MouseClick += (s, e) => { CloneProfile(); SetProfilesSaveStatus(false); };
            pictureBoxHelp.Click += PictureBoxHelp_Click;

            buttonSave.Click += ButtonSave_Click;
            buttonReset.Click += ButtonReset_Click;
            buttonRetry.Click += ButtonRetry_Click;
            buttonAlarm.Click += ButtonAlarm_Click;
            buttonJingle.Click += (s, e) => { OpenJingleSettings(); };

            comboBoxProfile.SelectedIndexChanged += ComboBoxProfile_SelectedIndexChanged;
            checkBoxShowYaw.CheckedChanged += CheckBoxShowYaw_CheckedChanged;
            checkBoxWindowsStart.CheckedChanged += CheckBoxWindowsStart_CheckedChanged;
            checkBoxSteamVRStart.CheckedChanged += CheckBoxSteamVRStart_CheckedChanged;
            checkBoxStartMinUser.CheckedChanged += CheckBoxStartMinUser_CheckedChanged;
            checkBoxStartMinAuto.CheckedChanged += CheckBoxStartMinWin_CheckedChanged;
            checkBoxConnLost.CheckedChanged += CheckBoxConnLost_CheckedChanged;
            checkBoxSticky.CheckedChanged += CheckBoxSticky_CheckedChanged;
            checkBoxOnAPIQuit.CheckedChanged += CheckBoxOnAPIQuit_CheckedChanged;
            checkBoxTrayNotifications.CheckedChanged += CheckBoxTrayNotifications_CheckedChanged;
            checkBoxPlaySoundOnHMDInteraction.CheckedChanged += CheckBoxPlayJingle_CheckedChanged;
            checkBoxRememberRotation.CheckedChanged += CheckBoxRememberRotation_CheckedChanged;
            numericUpDownRotMemory.ValueChanged += NumericUpDownRotMemory_ValueChanged;

            Observer.StateRefreshed += Observer_StateRefreshed;            
            profileEditor.ProfileNameChanged += (s, e) => { RefreshProfileCombo(); };
            profileEditor.ChangeMade += OnProfileChangeMade;
            profileEditor.VRConnectionParameterChanged += (s, e) => { RefreshVRConnectionForActiveProfile(); };
            OculusConn.StatusChanged += OnVRConnectionStatusChanged;
            OpenVRConn.StatusChanged += OnVRConnectionStatusChanged;
            OculusConn.StatusChangedToAllOK += (s,e) => { WaveOutPool.SendDeviceRefreshRequest(); };
            OculusConn.StatusChangedToNotOK += OnVRConnectionLost;
            OpenVRConn.StatusChangedToNotOK += OnVRConnectionLost;
            OculusConn.HMDUserInteractionStarted += OnHMDUserInteractionStarted;            
            OpenVRConn.HMDUserInteractionStarted += OnHMDUserInteractionStarted;
            OculusConn.HMDUserInteractionStopped += OnHMDUserInteractionStopped;
            OpenVRConn.HMDUserInteractionStopped += OnHMDUserInteractionStopped;

            TrayMenuReset.Click += TrayMenutReset_Click;
            TrayMenuAlarmClear.Click += TrayMenuAlarmClear_Click;            
            TrayMenuGUI.Click += (s,e) => { RestoreFromTray();};
            TrayMenuExit.Click += (s, e) => { Exit(); };
            TrayMenu.Opening += TrayMenu_Opening;

            AlarmTimer.Tick += AlarmTimer_Tick;
        }

       

        void Exit()
        {
            if (ForceHide) // form has never been shown
            {
                ExitRoutines();
                StartNewProcessIfRestart();
                Application.Exit();
            }
            else
            {
                Close(); // form close events contain exit routines & restart
            }            
        }

        void ExitRoutines()
        {
            Config.WriteConfigToFile(true); // to save last used profile etc.

            OculusConn.StatusChanged -= OnVRConnectionStatusChanged; // to prevent running eventhandler after form close
            OpenVRConn.StatusChanged -= OnVRConnectionStatusChanged;
            OpenVRConn?.Dispose();
            OculusConn?.Dispose();
            AlarmTimer.Dispose();
        }
       
        void AddEventHandlersCommon(Control ctl)
        {
            if (ctl is ComboBox)
            {
                ctl.MouseDown += (s, e) => { MouseDownOnComboBox = true; };                
            }

            foreach (Control item in ctl.Controls)
            {
                AddEventHandlersCommon(item);
            }
        }

        private void TrayMenu_Opening(object sender, CancelEventArgs e)
        {
            uint halfTurns = Tracker.CompletedHalfTurns;
            Direction rotSide = Tracker.RotationSide;
            TrayMenuRotations.Text = "Half turns: " + halfTurns.ToString() + ((halfTurns > 0) ? " (" + rotSide.ToString() + ")" : "");

            if (TimerHours == 0 && TimerMinutes == 0 && TimerSeconds == 0)
            {
                TrayMenuAlarmIn.Text = $"Alarm me in";                
                TrayMenuAlarmIn.ForeColor = Color.Empty;
                TrayMenuAlarmAt.Text = $"Alarm me @";
                TrayMenuAlarmAt.ForeColor = Color.Empty;
                TrayMenuAlarmClear.Text = $"Cancel alarm";
                TrayMenuAlarmClear.Enabled = false;
            }
            else
            {   
                TimeSpan remain = AlarmTime.Subtract(DateTime.Now);
                TrayMenuAlarmIn.Text = $"Alarm me in {remain.Hours}h {remain.Minutes}min {remain.Seconds}s";                
                TrayMenuAlarmIn.ForeColor = Config.CGColor;
                TrayMenuAlarmAt.Text = $"Alarm me @ {AlarmTime.ToShortTimeString()}";
                TrayMenuAlarmAt.ForeColor = Config.CGColor;
                TrayMenuAlarmClear.Text = $"Cancel alarm";
                TrayMenuAlarmClear.Enabled = true;
            }

            // update AM/PM for available alarm times (next 12h):
            DateTime now = DateTime.Now;
            int nowHour0_11 = (now.Hour > 11) ? now.Hour - 12 : now.Hour;
            int nowMin = now.Minute;
            bool isPM = (now.Hour > 11);
            string amPM = "AM";

            foreach (ToolStripMenuItem itemH in TrayMenuAlarmAt.DropDownItems)
            {
                int menuHour0_11 = (int)itemH.Tag;
                foreach (ToolStripMenuItem itemMin in itemH.DropDownItems)
                {
                    int menuMin = (int)itemMin.Tag;
                    if (menuHour0_11 < nowHour0_11)
                    {
                        amPM = (isPM) ? "AM" : "PM";
                    }
                    else if (menuHour0_11 > nowHour0_11)
                    {
                        amPM = (isPM) ? "PM" : "AM";
                    }
                    else // current hour
                    {
                        if (menuMin <= nowMin)
                        {
                            amPM = (isPM) ? "AM" : "PM";
                        }
                        else
                        {
                            amPM = (isPM) ? "PM" : "AM";
                        }
                    }
                    int menuH12 = (menuHour0_11 == 0) ? 12 : menuHour0_11;                    
                    itemMin.Text = menuH12.ToString() + ":" + ((menuMin < 10) ? "0" : "") + menuMin.ToString() + " " + amPM;
                }
            }
        }

        private void TrayMenutReset_Click(object sender, EventArgs e)
        {
            ResetRotations();
        }

        void ShowTemporaryTrayNotification(int timeOut, string title, string message, ToolTipIcon icon = ToolTipIcon.None)
        {
            notifyIcon1.ShowBalloonTip(timeOut, title, message, icon);
            // to clear the notification from the list:
            notifyIcon1.Visible = false;
            notifyIcon1.Visible = true;
        }

        private void TrayMenuAlarmInItem_Click(object sender, EventArgs e)
        {
            AlarmTimer.Stop();
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            ToolStripMenuItem parent = item.OwnerItem as ToolStripMenuItem;
            TimerHours = (int)parent.Tag;
            TimerMinutes = (int)item.Tag;
            AlarmTime = DateTime.Now.AddHours(TimerHours);
            AlarmTime = AlarmTime.AddMinutes(TimerMinutes);

            int interval = (TimerHours * 3600 * 1000) + (TimerMinutes * 60 * 1000);
            //int interval = (TimerHours * 3600 * 10) + (TimerMinutes * 60 * 10); // for testing

            SetAlarm(interval);
        }

        private void TrayMenuAlarmAtItem_Click(object sender, EventArgs e)
        {            
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            ToolStripMenuItem parent = item.OwnerItem as ToolStripMenuItem;
            int hours = (int)parent.Tag;
            int minutes = (int)item.Tag;
            DateTime now = DateTime.Now;
            // No more AM/PM menu. The nearest one is chosen automatically.
            DateTime AlarmTimeAM = new DateTime(now.Year, now.Month, now.Day, hours, minutes, 0);
            DateTime AlarmTimePM = new DateTime(now.Year, now.Month, now.Day, (hours == 0) ? 12 : hours + 12, minutes, 0);
            if (AlarmTimeAM < now)
                AlarmTimeAM = AlarmTimeAM.AddDays(1);
            if (AlarmTimePM < now)
                AlarmTimePM = AlarmTimePM.AddDays(1);
                        
            AlarmTime = (AlarmTimeAM < AlarmTimePM) ? AlarmTimeAM : AlarmTimePM;
            
            //AlarmTime = new DateTime(now.Year,now.Month,now.Day,hours,minutes,0);
            //if (AlarmTime < now)
            //    AlarmTime = AlarmTime.AddDays(1);
                        
            TimerHours = (AlarmTime - now).Hours;
            TimerMinutes = (AlarmTime - now).Minutes;
            TimerSeconds = (AlarmTime - now).Seconds;

            int interval = (TimerHours * 3600 * 1000) + (TimerMinutes * 60 * 1000) + (TimerSeconds * 1000);
            //int interval = (TimerHours * 3600 * 10) + (TimerMinutes * 60 * 10); // for testing
           
            SetAlarm(interval);         
        }

                        
        void SetAlarm(int interval)
        {
            AlarmTimer.Stop();
            if (interval > 0)
            {
                AlarmTimer.Interval = interval;
                AlarmTimer.Start();
                if (Config.TrayMenuNotifications)
                {
                    ShowTemporaryTrayNotification(5000, Config.ProgramTitle, $"Alarm will go off in {TimerHours}h {TimerMinutes}min {TimerSeconds}s (@ {AlarmTime.ToShortTimeString()}).");
                }
            }
            else
            {
                PlayAlarm();
            }
        }

        private void TrayMenuAlarmClear_Click(object sender, EventArgs e)
        {
            AlarmTimer.Stop();
            TimerHours = TimerMinutes = TimerSeconds = 0;
            if (Config.TrayMenuNotifications)            
                ShowTemporaryTrayNotification(2000, Config.ProgramTitle, "Alarm cancelled.");                
                        
        }
        
        private void AlarmTimer_Tick(object sender, EventArgs e)
        {
            PlayAlarm();
        }

        private void PlayAlarm()
        {
            AlarmTimer.Stop();
            TimerHours = TimerMinutes = TimerSeconds = 0;
            Config.Alarm.Play();
        }

        
        private void CheckBoxConnLost_CheckedChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;
                        
            Config.NotifyWhenVRConnectionLost = (checkBoxConnLost.Checked);
            SaveConfigurationToFile();
        }

        private void CheckBoxSticky_CheckedChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;

            Config.ConnLostNotificationIsSticky = (checkBoxSticky.Checked);
            SaveConfigurationToFile();
        }

        private void CheckBoxOnAPIQuit_CheckedChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;

            Config.NotifyOnAPIQuit = (checkBoxOnAPIQuit.Checked);
            SaveConfigurationToFile();
        }

        private void CheckBoxTrayNotifications_CheckedChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;

            Config.TrayMenuNotifications = (checkBoxTrayNotifications.Checked);
            SaveConfigurationToFile();
        }

        private void CheckBoxPlayJingle_CheckedChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;

            SetControlVisibility();
            Config.PlaySoundOnHMDinteractionStart = (checkBoxPlaySoundOnHMDInteraction.Checked);
            SaveConfigurationToFile();
        }

        private void CheckBoxRememberRotation_CheckedChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;
            
            SetControlVisibility();            
            Config.TurnCountMemoryMinutes = (checkBoxRememberRotation.Checked) ? (int)numericUpDownRotMemory.Value : -1 ;            
            SaveConfigurationToFile();
        }

        private void NumericUpDownRotMemory_ValueChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;

            SetControlVisibility();
            Config.TurnCountMemoryMinutes = (int)numericUpDownRotMemory.Value;
            SaveConfigurationToFile();
        }

        private void ButtonRetry_Click(object sender, EventArgs e)
        {
            Enabled = false;
            ActiveConnection?.Open();
            Enabled = true;
        }

        private void PictureBoxHelp_Click(object sender, EventArgs e)
        {
            FormHelp help = new FormHelp();
            help.StartPosition = FormStartPosition.CenterParent;
            SkipFlaggedEventHandlers = true;
            //TrayMenu.Enabled = false;
            help.ShowDialog(this);
            //TrayMenu.Enabled = true;
            SkipFlaggedEventHandlers = false;
        }

        void SetProfilesSaveStatus(bool saved)
        {
            ProfilesSaved = saved;
            buttonSave.ForeColor = (saved) ? Config.CGColor : Config.CGErrorColor ;
        }

        void AddProfile()
        {
            Profile prof = new Profile();
            Config.AddProfile(prof);
            
            RefreshProfileCombo();

            SkipFlaggedEventHandlers = true;
            comboBoxProfile.SelectedItem = prof;
            SkipFlaggedEventHandlers = false;

            LoadProfile(prof);            
        }

        void CloneProfile()
        {
            Profile actP = Config.ActiveProfile;
            if (actP == null)
                return;

            Profile newP = new Profile();
            newP.LoadFromXml(actP.GetXml());
            newP.Name = "Clone of " + actP.Name;
            Config.AddProfile(newP);

            RefreshProfileCombo();

            SkipFlaggedEventHandlers = true;
            comboBoxProfile.SelectedItem = newP;
            SkipFlaggedEventHandlers = false;

            LoadProfile(newP);
        }


        private void ButtonSave_Click(object sender, EventArgs e)
        {
            SaveProfilesToFile();
        }


        private void ButtonAlarm_Click(object sender, EventArgs e)
        {
            OpenAlarmSettings();   
        }

        private void OpenAlarmSettings()
        {
            ShowSoundFormAndSaveConfig(PointToScreen(new Point(buttonAlarm.Location.X - 2, buttonAlarm.Location.Y - 2)), Config.Alarm, 
                                                    $"Audio device is taken from the active profile.{Environment.NewLine}Use the tray icon to set the alarm.");
        }

        private void OpenJingleSettings()
        {
            ShowSoundFormAndSaveConfig(PointToScreen(new Point(buttonJingle.Location.X - 2, buttonJingle.Location.Y - 2)), Config.Jingle,
                                                    "Audio device is taken from the active profile.");         
        }

        void ShowSoundFormAndSaveConfig(Point location, CGActionWave waveAction, string infoText = "")
        {
            FormSound frm = new FormSound(waveAction, infoText);
            frm.StartPosition = FormStartPosition.Manual;
            frm.Location = location;
            SkipFlaggedEventHandlers = true;
            TrayMenu.Enabled = false;
            frm.ShowDialog(this);
            TrayMenu.Enabled = true;
            SkipFlaggedEventHandlers = false;
            SaveConfigurationToFile();
        }

        private void RefreshVRConnectionForActiveProfile()
        { 
            if (Config.ActiveProfile.API == VRAPI.OculusVR)
                SwitchVRConnection(OculusConn);
            else
                SwitchVRConnection(OpenVRConn);

            OculusConn.RequireHome = Config.ActiveProfile.RequireHome;            
        }
                              
        
        /// <summary>
        /// Opens a VR connection for tracking the rotation (unless already open). 
        /// If another connection is open, it will be closed first.
        /// </summary>
        /// <param name="api"></param>
        void SwitchVRConnection(VRConnection connectionToOpen)
        {
            if (ActiveConnection == connectionToOpen)
                return;

            Enabled = false;            
            VRConnection connToClose;
            
            if (connectionToOpen == OculusConn) 
            {                
                pictureBoxLogo.Image = Properties.Resources.CGLogo;
                checkBoxSteamVRStart.Visible = (checkBoxSteamVRStart.Checked);
                pictureBoxSteamVRStartUp.Visible = (pictureBoxSteamVRStartUp.Visible && checkBoxSteamVRStart.Visible);
                connToClose = OpenVRConn;                
            }
            else
            {             
                pictureBoxLogo.Image = Properties.Resources.CGLogo_Index;                
                connToClose = OculusConn;                
            }            
                        
            connToClose.Close();
                        
            if (connectionToOpen.Status != VRConnectionStatus.Closed)
                connectionToOpen.Close();

            connectionToOpen.Open();
            Observer.SetVRConnection(connectionToOpen);
            ActiveConnection = connectionToOpen;

            Enabled = true;
            Cursor.Current = Cursors.Default;
            
            // Force GUI refresh in case the connection has stopped reporting status earlier
            OnVRConnectionStatusChanged(connectionToOpen, null);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S))
            {
                SaveProfilesToFile();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void OnProfileChangeMade(object sender, ChangeEventArgs e)
        {
            SetProfilesSaveStatus(false);
        }

        private void CheckBoxStartMinUser_CheckedChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;

            Config.MinimizeAtUserStartup = checkBoxStartMinUser.Checked;
            SaveConfigurationToFile();
        }

        private void CheckBoxStartMinWin_CheckedChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;

            Config.MinimizeAtAutoStartup = checkBoxStartMinAuto.Checked;
            SaveConfigurationToFile();
        }

        private void PictureBoxMinus_Click(object sender, EventArgs e)
        {
            Profile selProf = comboBoxProfile.SelectedItem as Profile;
            if (selProf != null)
            {
                bool del = false;
                string msg = $"Delete profile \"{selProf.Name}\"?";
                if (MessageBox.Show(this, msg, Config.ProgramTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
                {
                    if (selProf.Frozen)
                    {
                        msg = $"Profile \"{selProf.Name}\" has been frozen to prevent accidental changes. Delete anyway?";
                        if (MessageBox.Show(this, msg, Config.ProgramTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
                            del = true;
                    }
                    else
                    {
                        del = true;
                    }
                }                

                if (del)
                {
                    DeleteProfile(selProf);
                    SetProfilesSaveStatus(false);
                }                
            }            
        }

       
        void DeleteProfile(Profile profile)
        {
            if (profile == null)
                throw new Exception("null profile cannot be deleted");

            Profile other = Config.Profiles.Where(p => p != profile).FirstOrDefault();

            if (other != null)
            {
                //LoadProfile(other);
                comboBoxProfile.SelectedItem = other;
            }
            else
                AddProfile();

            Config.RemoveProfile(profile);
            profile.Dispose();

            RefreshProfileCombo();
        }

        private void ComboBoxProfile_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;

            bool saveStatus = ProfilesSaved;
            LoadProfile(comboBoxProfile.SelectedItem as Profile);
            SetProfilesSaveStatus(saveStatus);
        }

        void LoadProfile(Profile p)
        {
            if (p != null)
            {
                if (ActiveConnection?.Status == VRConnectionStatus.AllOK)
                {
                    if (p.API != Config.ActiveProfile?.API)
                    {
                        IntentionalAPIChange = true; // dirty way to prevent connection lost notification on user interaction
                    }
                }
                
                profileEditor.Visible = true;
                Config.SetActiveProfile(p);
                profileEditor.LoadProfile(p);

                WaveOutPool.WaveOutDeviceSource = p.WaveOutDeviceSource;
                if (p.WaveOutDeviceSource == AudioDeviceSource.Manual)
                    WaveOutPool.SetWaveOutDevice(p.TheWaveOutDevice);

                RefreshVRConnectionForActiveProfile(); // after audio device has been set (to refresh Oculus Home audio)

                if (p.OriginalWaveOutDeviceNotFound)
                {
                    string msg = $"Audio device for the profile \"{p.Name}\" was not found!{Environment.NewLine}Device name: \"{p.NotFoundDeviceName}\"";
                    Config.WriteLog(msg);                    
                    
                    RestoreFromTray();                    
                    notifyIcon1.ShowBalloonTip(4000, Config.ProgramTitle, msg, ToolTipIcon.Warning);                    
                }
            }
            else
            {
                profileEditor.Visible = false;
            }
        }

        void RefreshProfileCombo()
        {
            SkipFlaggedEventHandlers = true;

            object selected = comboBoxProfile.SelectedItem;
            comboBoxProfile.DataSource = null;
            comboBoxProfile.DataSource = Config.Profiles;

            if (selected != null && comboBoxProfile.Items.Contains(selected))
            {
                comboBoxProfile.SelectedItem = selected;
            }

            SkipFlaggedEventHandlers = false;
        }

        private void CheckBoxWindowsStart_CheckedChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)            
                return;
                        
            try
            {
                Config.WriteWindowsStartupToRegistry(checkBoxWindowsStart.Checked);                
            }
            catch (Exception ex)
            {
                string msg = String.Format("Unable* to access registry to set startup status. Try running this app as Administrator.{0}{0}", Environment.NewLine);
                msg += String.Format("Alternatively, you can manually add a shortcut into your startup folder: {1}{0}{0}" 
                                    + "Point the shortcut to: {2}{0}{0}Remember to add the \"{3}\" -parameter! {0}{0}" 
                                     ,Environment.NewLine, Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                                       "\"" + Program.ExeFile + "\" " + Program.Arg_WinStartup, Program.Arg_WinStartup);                
                msg += "*" + ex.Message;
                MessageBox.Show(this, msg, Config.ProgramTitle);
            }

            CheckWindowsStartUpStatus();
        }

        void CheckWindowsStartUpStatus()
        {
            bool check = false;
            try
            {
                check = Config.ReadWindowsStartupFromRegistry();              
            }
            catch (Exception)
            {
                // intentionally ignore
            }

            SkipFlaggedEventHandlers = true;
            checkBoxWindowsStart.Checked = check;
            SkipFlaggedEventHandlers = false;
        }
        
        private void CheckBoxSteamVRStart_CheckedChanged(object sender, EventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;
          
            try
            {
                if (OpenVRConn.Status != VRConnectionStatus.AllOK)
                    throw new Exception("OpenVR connection not established.");

                if (checkBoxSteamVRStart.Checked)
                {
                    Config.WriteManifestFile();
                    OpenVRConn.SetSteamVRAutoStart(true);
                }
                else
                {
                    OpenVRConn.SetSteamVRAutoStart(false);
                }
                pictureBoxSteamVRStartUp.Visible = false;
                CheckSteamVRStartUpStatus(checkBoxSteamVRStart.Checked);
            }
            catch (Exception ex)
            {
                string msg = $"Unable to configure SteamVR startup.{Environment.NewLine}{ex.Message}";
                TTip.SetToolTip(pictureBoxSteamVRStartUp, msg);                    
                pictureBoxSteamVRStartUp.Visible = true;

                SkipFlaggedEventHandlers = true;
                checkBoxSteamVRStart.Checked = !checkBoxSteamVRStart.Checked;
                SkipFlaggedEventHandlers = false;
            }                   
        }

        void CheckSteamVRStartUpStatus(bool? expectedStatus = null)
        {
            if (OpenVRConn.Status != VRConnectionStatus.AllOK)
                return;

            checkBoxSteamVRStart.Visible = true;
            pictureBoxSteamVRStartUp.Visible = false;

            bool check = false;
            try
            {
                check = OpenVRConn.IsSteamAutoStartEnabled();
            }
            catch (Exception)
            {
                // intentionally ignore
            }

            SkipFlaggedEventHandlers = true;
            checkBoxSteamVRStart.Checked = check;
            SkipFlaggedEventHandlers = false;

            if (expectedStatus != null && check != (bool)expectedStatus)
            {
                string msg = "Verifying SteamVR startup status failed. Please try again." + Environment.NewLine + Environment.NewLine
                            + $"NOTE that SteamVR auto start doesn't seem to work if there are non-ASCII characters (umlauts and such) in the {Config.ProgramTitle} path." + Environment.NewLine                            
                            + $"Current path: \"{Program.ExeFolder}\"";
                TTip.SetToolTip(pictureBoxSteamVRStartUp, msg);
                pictureBoxSteamVRStartUp.Visible = true;
            }
        }


        private void CheckBoxShowYaw_CheckedChanged(object sender, EventArgs e)
        {
            UpdateYawToForm = checkBoxShowYaw.Checked;
            labelYaw.Visible = checkBoxShowYaw.Checked;                        
            labelHalfTurnTitle.Visible = checkBoxShowYaw.Checked;
            labelHalfTurns.Visible = checkBoxShowYaw.Checked;
        }

        void Observer_StateRefreshed(object sender, VRObserverEventArgs e)
        {
            if (UpdateYawToForm)
            {
                labelYaw.Text = YawTracker.RadToDeg(Tracker.YawValue).ToString();
                labelHalfTurns.Text = Tracker.CompletedHalfTurns.ToString() + ((Tracker.CompletedHalfTurns > 0) ? " (" + Tracker.RotationSide.ToString() + ")" : "");
            }
        }

        void AddDragEventHandlers()
        {
            MouseMove += DragPoint_MouseMove;
            MouseDown += DragPoint_MouseDown;
            pictureBoxLogo.MouseDown += DragPoint_MouseDown;
            pictureBoxLogo.MouseMove += DragPoint_MouseMove;

            foreach (Control ctl in Controls)
            {
                if (ctl is Label)
                {
                    ctl.MouseDown += DragPoint_MouseDown;
                    ctl.MouseMove += DragPoint_MouseMove;
                }                
            }
        }

        void OnHMDUserInteractionStarted(object sender, EventArgs e)
        {
            if (Config.PlaySoundOnHMDinteractionStart)
            {
                Config.Jingle.Play();
            }
        }

        void OnHMDUserInteractionStopped(object sender, EventArgs e)
        {
            
        }

        void OnVRConnectionStatusChanged(object sender, EventArgs e)
        {
            VRConnection conn = sender as VRConnection;

            if (conn != ActiveConnection)
                return;

            if (conn.Status != VRConnectionStatus.AllOK)
            {
                Icon = CableGuardian.Properties.Resources.CG_error;
                labelStatus.ForeColor = Config.CGErrorColor;
                labelYaw.ForeColor = Config.CGErrorColor;
                labelHalfTurns.ForeColor = Config.CGErrorColor;
                labelHalfTurnTitle.ForeColor = Config.CGErrorColor;
                notifyIcon1.Icon = CableGuardian.Properties.Resources.CG_error;
                notifyIcon1.Text = Config.ProgramTitle + $": {Config.ActiveProfile.API} - {conn.Status.ToString()}";
                TrayMenuTitle.ForeColor = Config.CGErrorColor;
                TrayMenuTitle.Text = Config.ProgramTitle + " - NOT OK";
                // to ensure the app is shutdown after auto-start if SteamVR disappears without a quit message (probably never happens)
                if (OpenVRConn.OpenVRConnStatus == OpenVRConnectionStatus.NoSteamVR && Program.IsSteamVRStartup)
                {
                    ProfilesSaved = true; // bypass save dialog if not saved                             
                    Exit();                    
                }
            }
            else
            {
                Icon = CableGuardian.Properties.Resources.CG;
                labelStatus.ForeColor = Config.CGColor;
                labelYaw.ForeColor = Config.CGColor;
                labelHalfTurns.ForeColor = Config.CGColor;
                labelHalfTurnTitle.ForeColor = Config.CGColor;
                notifyIcon1.Icon = CableGuardian.Properties.Resources.CG;
                notifyIcon1.Text = Config.ProgramTitle + $": {Config.ActiveProfile.API} - {conn.Status.ToString()}";
                TrayMenuTitle.ForeColor = Config.CGColor;
                TrayMenuTitle.Text = Config.ProgramTitle + " - All OK";
                CheckSteamVRStartUpStatus();
            }

            labelStatus.Text = $"VR Headset Connection Status:{Environment.NewLine}{Environment.NewLine}" + conn.StatusMessage;
                       
            buttonRetry.Visible = (conn.Status == VRConnectionStatus.Closed);

            if (conn.Status == VRConnectionStatus.InitLimitReached)
            {
                Restart();
            }            
        }

        void OnVRConnectionLost(object sender, EventArgs e)
        {
            if (!IntentionalAPIChange)
            {
                bool controlledAPIQuit = false;
                // a bit hacky and lazy way to check if API requested a controlled quit
                if (sender == OculusConn && OculusConn.OculusStatus == OculusConnectionStatus.OculusVRQuit)                
                    controlledAPIQuit = true;                
                else if(sender == OpenVRConn && OpenVRConn.OpenVRConnStatus == OpenVRConnectionStatus.SteamVRQuit)
                    controlledAPIQuit = true;


                bool show = false;
                if (Config.NotifyWhenVRConnectionLost && !controlledAPIQuit)                
                    show = true;

                if (Config.NotifyOnAPIQuit && controlledAPIQuit)
                    show = true;                

                if (show)
                {   
                    string msg = $"VR headset connection lost. {Config.ProgramTitle} offline.";
                    if (Config.ConnLostNotificationIsSticky)
                        notifyIcon1.ShowBalloonTip(4000, Config.ProgramTitle, msg, ToolTipIcon.Warning);
                    else
                        ShowTemporaryTrayNotification(2000, Config.ProgramTitle, msg, ToolTipIcon.Warning);

                    System.Threading.Thread.Sleep(1000);
                    Config.ConnLost.Play();
                }

                // Close program if it was started automatically with SteamVR
                if (controlledAPIQuit && Program.IsSteamVRStartup)
                {
                    ProfilesSaved = true; // bypass save dialog if not saved                              
                    Exit();
                }
            }

            IntentionalAPIChange = false;
        }                

        void Restart()
        {
            RestartArgs = Program.Arg_IsRestart;
            RestartArgs += (Visible) ? Program.Arg_Maximized : Program.Arg_Minimized;
            RestartArgs += Tracker.CurrentHalfTurn.ToString(); 
            ProfilesSaved = true; // bypass save dialog if not saved            
            IsRestart = true;
            Exit();
        }

        private void PictureBoxClose_MouseClick(object sender, MouseEventArgs e)
        {
            Exit();
        }

        void MinimizeToTray()
        {
            //WindowState = FormWindowState.Minimized;
            //ShowInTaskbar = false;
            Hide();
        }
        void RestoreFromTray()
        {
            //WindowState = FormWindowState.Normal;
            //ShowInTaskbar = true;         
            ForceHide = false;
            Show();
        }

        private void NotifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (SkipFlaggedEventHandlers)
                return;

            //if (WindowState == FormWindowState.Normal)
            if (Visible)
                MinimizeToTray();
            else
                RestoreFromTray();            
        }

        private void PictureBoxMinimize_MouseClick(object sender, MouseEventArgs e)
        {
            MinimizeToTray();
        }

        private void DragPoint_MouseDown(object sender, MouseEventArgs e)
        {            
            MouseDragPosOnForm = PointToClient(Cursor.Position);
            MouseDownOnComboBox = false;
        }

        private void DragPoint_MouseMove(object sender, MouseEventArgs e)
        {
            if (MouseDownOnComboBox) // comboboxes were causing some erratic form movement
            {                
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                Location = new Point(Cursor.Position.X - MouseDragPosOnForm.X, Cursor.Position.Y - MouseDragPosOnForm.Y);
                
            }
        }

        private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Warn about unsaved profiles except in case of win shutdown. 
            // Preventing shutdown might feel more annoying than losing (most likely unimportant?) changes to profiles...             
            if (!ProfilesSaved && e.CloseReason != CloseReason.WindowsShutDown) 
            {                
                string msg = String.Format("There are unsaved changes to your profiles. Close anyway?");
                e.Cancel = (MessageBox.Show(this, msg, Config.ProgramTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Cancel);                                    
            }

            if (!e.Cancel)
            {
                ExitRoutines();
                
                // Restore the following bit if save warning is shown during win shutdown (see above):
                //if (e.CloseReason == CloseReason.WindowsShutDown)
                //{
                //    Environment.Exit(0); // for some reason the application did not close on windows shutdown when save warning was shown
                //}
            }
        }

        private void FormMain_FormClosed(object sender, FormClosedEventArgs e)
        {               
            StartNewProcessIfRestart();
        }

        void StartNewProcessIfRestart()
        {
            notifyIcon1.Visible = false;
            if (IsRestart)
            {
                try
                {
                    System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo(Program.ExeFile, RestartArgs);
                    System.Diagnostics.Process.Start(startInfo);
                }
                catch (Exception)
                {
                    // intentionally ignore
                }

                IsRestart = false;
            }
        }

        void SaveConfigurationToFile()
        {
            try
            {
                Config.WriteConfigToFile();
            }
            catch (Exception e)
            {
                string msg = $"Saving configuration to {Program.ConfigFile} failed! {Environment.NewLine}{Environment.NewLine} {e.Message}";
                MessageBox.Show(this, msg, Config.ProgramTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
           
        }

        void SaveProfilesToFile()
        {
            try
            {
                Config.WriteProfilesToFile();
                SetProfilesSaveStatus(true);
            }
            catch (Exception e)
            {
                string msg = $"Saving profiles to {Config.ProfilesFile} failed! {Environment.NewLine}{Environment.NewLine} {e.Message}";
                MessageBox.Show(this, msg, Config.ProgramTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }            
        }

        void ResetRotations()
        {
            Tracker.Reset();
            labelHalfTurns.Text = "0";
            if (Config.TrayMenuNotifications)
                ShowTemporaryTrayNotification(2000, Config.ProgramTitle, "Reset successful. Turn count = 0.");
        }

        private void ButtonReset_Click(object sender, EventArgs e)
        {
            ResetRotations();

            if (Config.ShowResetMessageBox)
            {
                string msg = String.Format("Turn counter has been reset to zero. " +
                                            "It is assumed that the headset cable is currently completely untwisted.{0}{0}" +
                                            "Note that the reset position is always set to 0 degrees (facing forward) " +
                                            "regardless of the headset orientation when applying this reset operation. Also note that by default " +
                                            "the turn counter is reset when {1} is started. You can change this behaviour with the \"Remember turn count\" -feature. " +
                                            "You can perform the reset from the tray icon as well. {0}{0}" +
                                            "Hide this message in the future?", Environment.NewLine, Config.ProgramTitle);
                if (MessageBox.Show(this, msg, Config.ProgramTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    Config.ShowResetMessageBox = false;
                    SaveConfigurationToFile();
                }
            }
        }        
    }
}
