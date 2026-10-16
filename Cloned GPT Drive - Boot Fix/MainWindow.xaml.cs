using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using System.Management;
using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cloned_GPT_Drive___Boot_Fix
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Constants
        private const string DISKPART_PATH = @"C:\Windows\System32\diskpart.exe";
        private const string CMD_PATH = @"C:\Windows\System32\cmd.exe";
        private const string DISKMGMT_PATH = @"C:\Windows\System32\diskmgmt.msc";

        public string logString = "";

        /// <summary>
        /// A physical disk as Windows (WMI) sees it. Its Number is the same disk number diskpart uses.
        /// </summary>
        private class PhysicalDisk
        {
            public int Number;
            public string Model = "";
            public string SerialNumber = "";
            public ulong SizeBytes;
            public string Key = "";         // stable hardware identity, see DiskpartParser.BuildDiskKey
            public List<string> Letters = new List<string>();
        }

        /// <summary>
        /// A disk picked in the Extras tab dropdown, remembered together with everything that
        /// identified it when the list was built: disk number, hardware identity, diskpart
        /// Disk ID (GPT GUID / MBR signature) and drive letters. All of them must still match
        /// before anything destructive runs.
        /// </summary>
        private class DiskChoice
        {
            public int Number;
            public string Key = "";
            public string DiskId = "";
            public List<string> Letters = new List<string>();
        }

        /// <summary>
        /// The result of checking a disk right before a destructive operation. Any block reason
        /// means the operation must not run.
        /// </summary>
        private class DiskSafetyCheck
        {
            public int Number;
            public PhysicalDisk Disk;       // null if Windows could not find the disk
            public DiskDetail Detail;
            public List<string> Letters = new List<string>();
            public List<string> BlockReasons = new List<string>();

            public bool IsBlocked => BlockReasons.Count > 0;
        }

        private readonly List<VolumeEntry> _parsedVolumes = new List<VolumeEntry>();
        private Dictionary<int, int> _volumeToDisk = new Dictionary<int, int>();

        // Each disk's details and hardware identity as of the last Refresh, so Fix Boot can
        // check that disk numbers still point at the same disks before it changes anything
        private Dictionary<int, DiskDetail> _diskDetails = new Dictionary<int, DiskDetail>();
        private Dictionary<int, PhysicalDisk> _physicalDisks = new Dictionary<int, PhysicalDisk>();

        public MainWindow()
        {
            InitializeComponent();

            // A fresh settings store can mean a version upgrade rather than a true first
            // run — user settings live in a per-version folder, so pull the previous
            // version's values (protected drives!) forward before deciding anything.
            if (Properties.Settings.Default.IsFirstRun)
            {
                try { Properties.Settings.Default.Upgrade(); } catch { }
            }

            // Initialize protected drives collection if null
            if (Properties.Settings.Default.ProtectedDrives == null)
            {
                Properties.Settings.Default.ProtectedDrives = new System.Collections.Specialized.StringCollection();
                Properties.Settings.Default.Save();
            }

            // Check for first run
            if (Properties.Settings.Default.IsFirstRun)
            {
                if (ShowFirstRunDialog())
                {
                    Properties.Settings.Default.IsFirstRun = false;
                    Properties.Settings.Default.Save();
                }
            }
            else if (Properties.Settings.Default.ProtectedDisks == null)
            {
                // First launch after upgrading from a version that only protected letters
                ProtectDisksOfProtectedLetters();
            }

            RefreshDriveDropdowns();
            RemoveNetworkDrivesFromProtectedList();
            RefreshProtectedDrivesList();

            // Initial refresh of volumes on launch
            Loaded += (s, e) => Refresh_btn_Click(this, new RoutedEventArgs());
        }

        /// <summary>
        /// Updates the status text and progress bar shown below the top controls.
        /// </summary>
        private void SetProgress(string statusText, int? percent = null, bool indeterminate = false)
        {
            if (Progress_StatusText != null)
                Progress_StatusText.Text = statusText;

            if (Progress_Bar != null)
            {
                Progress_Bar.IsIndeterminate = indeterminate;
                if (!indeterminate && percent.HasValue)
                    Progress_Bar.Value = percent.Value;
            }
        }

        /// <summary>
        /// Extracts the drive letter (e.g. "C") from a combo box display string, ignoring any
        /// leading icon prefix (such as the 🪟 Windows indicator).
        /// </summary>
        private string ExtractDriveLetter(string comboBoxText)
        {
            if (string.IsNullOrEmpty(comboBoxText))
                return string.Empty;

            var match = Regex.Match(comboBoxText, @"[A-Za-z](?=:)");
            return match.Success ? match.Value : comboBoxText.Substring(0, 1);
        }

        /// <summary>
        /// Returns true only if the drive holds a real (bootable/cloned) Windows installation,
        /// not just any folder named "Windows" (e.g. an archive or VM storage folder).
        /// Checks for the NT kernel, the SYSTEM registry hive, and explorer.exe.
        /// </summary>
        private bool IsWindowsInstallation(string driveRoot)
        {
            try
            {
                string windowsDir = System.IO.Path.Combine(driveRoot, "Windows");
                if (!Directory.Exists(windowsDir))
                    return false;

                // In a 32-bit process on 64-bit Windows, the host's System32 is silently
                // redirected to SysWOW64 (which has no config\SYSTEM); go through Sysnative
                // to see the real System32 of the running OS
                string system32 = System.IO.Path.Combine(windowsDir, "System32");
                if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem
                    && string.Equals(windowsDir, Environment.GetEnvironmentVariable("windir"), StringComparison.OrdinalIgnoreCase))
                {
                    system32 = System.IO.Path.Combine(windowsDir, "Sysnative");
                }

                // Check the config DIRECTORY, not the SYSTEM hive file inside it — the hive
                // is ACL-restricted and File.Exists reports false on access-denied
                return File.Exists(System.IO.Path.Combine(system32, "ntoskrnl.exe"))
                    && Directory.Exists(System.IO.Path.Combine(system32, "config"))
                    && File.Exists(System.IO.Path.Combine(windowsDir, "explorer.exe"));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Populates the cloned-drive dropdown and the list of currently unused drive letters.
        /// Safe to call again later (e.g. after plugging in a drive) since it clears first.
        /// </summary>
        private void RefreshDriveDropdowns()
        {
            // Remember the current selection so refreshing (e.g. after protecting a
            // drive) doesn't wipe the user's choice
            string previousLetter = DriveSelection_ddbox.SelectedItem != null
                ? ExtractDriveLetter(DriveSelection_ddbox.SelectedItem.ToString())
                : null;

            DriveSelection_ddbox.Items.Clear();

            foreach (var drive in Environment.GetLogicalDrives())
            {
                // Local disks only: network drives are never offered (their letters are
                // simply in use, so they also never show up as free temporary letters)
                DriveInfo driveInfo = new DriveInfo(drive);
                if (IsLocalDrive(driveInfo) && driveInfo.IsReady)
                {
                    // Markers go at the END, tight after the name/label; the 🪟 emoji
                    // is too new for the default font and rendered as a square
                    var displayParts = new List<string> { driveInfo.Name };
                    if (!string.IsNullOrEmpty(driveInfo.VolumeLabel))
                        displayParts.Add(driveInfo.VolumeLabel);
                    if (IsWindowsInstallation(drive))
                        displayParts.Add("★ Windows");
                    if (IsDriveProtected(drive))
                        displayParts.Add("🔒");

                    DriveSelection_ddbox.Items.Add(string.Join("  ", displayParts));
                }
            }

            if (!string.IsNullOrEmpty(previousLetter))
            {
                foreach (var item in DriveSelection_ddbox.Items)
                {
                    if (ExtractDriveLetter(item.ToString()) == previousLetter)
                    {
                        DriveSelection_ddbox.SelectedItem = item;
                        break;
                    }
                }
            }

            UnusedDriveSelection_ddbox.Items.Clear();

            ArrayList driveLetters = new ArrayList(26); // Allocate space for alphabet
            for (int i = 65; i < 91; i++) // increment from ASCII values for A-Z
            {
                driveLetters.Add(Convert.ToChar(i)); // Add uppercase letters to possible drive letters
            }

            foreach (char usedLetter in GetLettersInUse())
            {
                driveLetters.Remove(usedLetter); // removed used drive letters (incl. mapped network drives) from possible drive letters
            }

            foreach (char drive in driveLetters)
            {
                UnusedDriveSelection_ddbox.Items.Add(drive + ":\\"); // add unused drive letters to the combo box
            }
        }

        /// <summary>
        /// Every drive letter that is taken, so it is never offered as a temporary letter. This
        /// app runs elevated, and an elevated process doesn't see network drives mapped in the
        /// user's normal session, so the users' saved network mappings are read from the registry too.
        /// </summary>
        private static HashSet<char> GetLettersInUse()
        {
            var used = new HashSet<char>();

            foreach (string drive in Directory.GetLogicalDrives())
                used.Add(char.ToUpper(drive[0]));

            try
            {
                // HKEY_USERS\<user>\Network\<letter> — saved mapped drives of every loaded user profile
                foreach (string userKey in Microsoft.Win32.Registry.Users.GetSubKeyNames())
                {
                    try
                    {
                        using (var network = Microsoft.Win32.Registry.Users.OpenSubKey(userKey + @"\Network"))
                        {
                            if (network == null)
                                continue;

                            foreach (string letter in network.GetSubKeyNames())
                            {
                                if (letter.Length == 1 && char.IsLetter(letter[0]))
                                    used.Add(char.ToUpper(letter[0]));
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                // Network drives currently mapped in any logon session (including ones that
                // aren't saved, e.g. 'net use /persistent:no' or logon scripts)
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_MappedLogicalDisk"))
                {
                    foreach (ManagementObject mapped in searcher.Get())
                    {
                        string name = Convert.ToString(mapped["Name"]);
                        if (name.Length > 0 && char.IsLetter(name[0]))
                            used.Add(char.ToUpper(name[0]));
                    }
                }
            }
            catch { }

            return used;
        }

        /// <summary>
        /// True for drives on a local disk (fixed or removable). This is a local disk tool —
        /// network, optical and RAM drives are never offered as targets.
        /// </summary>
        private static bool IsLocalDrive(DriveInfo driveInfo)
        {
            try
            {
                return driveInfo.DriveType == DriveType.Fixed || driveInfo.DriveType == DriveType.Removable;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Older versions added mapped network drives to the protected list. They are not local
        /// disks and are no longer shown at all, so drop entries for letters that are network drives.
        /// </summary>
        private void RemoveNetworkDrivesFromProtectedList()
        {
            try
            {
                bool removed = false;

                foreach (var drive in Environment.GetLogicalDrives())
                {
                    try
                    {
                        var driveInfo = new DriveInfo(drive);
                        if (driveInfo.DriveType == DriveType.Network
                            && Properties.Settings.Default.ProtectedDrives.Contains(drive))
                        {
                            Properties.Settings.Default.ProtectedDrives.Remove(drive);
                            removed = true;
                        }
                    }
                    catch { }
                }

                if (removed)
                    Properties.Settings.Default.Save();
            }
            catch { }
        }

        /// <summary>
        /// Returns the physical disk number a drive letter lives on, or null if unknown.
        /// </summary>
        private int? GetDiskNumberForLetter(string letter)
        {
            try
            {
                string query = $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}:'}} WHERE AssocClass=Win32_LogicalDiskToPartition";
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (ManagementObject partition in searcher.Get())
                    {
                        return Convert.ToInt32(partition["DiskIndex"]);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// What a row in the protected list stands for: a whole physical disk (by hardware key)
        /// or a single drive letter.
        /// </summary>
        private class ProtectedListItem
        {
            public string DiskKey;          // set for a protected disk
            public string DiskName = "";
            public string Letter;           // set for a protected letter, e.g. "C:\"
            public List<string> Letters = new List<string>();   // letters the disk has right now
        }

        /// <summary>
        /// Refreshes the protected drives list box from settings. Protected disks are shown with
        /// their current disk number, model, size and letters, e.g. "Disk 1 — Samsung SSD (465 GB)  C: D:".
        /// Letters are listed on their own only when they are not on a protected disk (e.g. a
        /// protected letter whose drive is not connected). Each item's Tag says what it stands for.
        /// </summary>
        private void RefreshProtectedDrivesList()
        {
            ProtectedDrives_ListBox.Items.Clear();

            List<PhysicalDisk> connectedDisks = GetAllPhysicalDisks();
            var lettersOnProtectedDisks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ProtectedDiskEntry entry in GetProtectedDisks()
                .OrderBy(en => connectedDisks.FirstOrDefault(d => d.Key == en.Key)?.Number ?? int.MaxValue))
            {
                PhysicalDisk disk = connectedDisks.FirstOrDefault(d => d.Key == entry.Key);
                string display = disk != null
                    ? $"Disk {disk.Number} — {DescribeDisk(disk)}  {FormatLetters(disk.Letters)}"
                    : $"Not connected — {entry.Name}";

                if (disk != null)
                    lettersOnProtectedDisks.UnionWith(disk.Letters);

                ProtectedDrives_ListBox.Items.Add(new ListBoxItem
                {
                    Content = "🔒 " + display,
                    Tag = new ProtectedListItem { DiskKey = entry.Key, DiskName = entry.Name, Letters = disk != null ? disk.Letters : new List<string>() },
                    ToolTip = display
                });
            }

            if (Properties.Settings.Default.ProtectedDrives == null)
                return;

            var protectedInfo = new List<Tuple<string, int?, string>>();

            foreach (string drive in Properties.Settings.Default.ProtectedDrives)
            {
                string letter = drive.Substring(0, 1).ToUpper();
                if (lettersOnProtectedDisks.Contains(letter + ":\\"))
                    continue;

                string label = "";
                try
                {
                    var driveInfo = new DriveInfo(letter + ":\\");
                    if (driveInfo.DriveType == DriveType.Network)
                        continue;
                    if (driveInfo.IsReady)
                        label = driveInfo.VolumeLabel;
                }
                catch { }

                protectedInfo.Add(Tuple.Create(letter, GetDiskNumberForLetter(letter), label));
            }

            // Sorted by disk number; letters not on any connected disk at the bottom
            foreach (var info in protectedInfo.OrderBy(t => t.Item2 ?? int.MaxValue).ThenBy(t => t.Item1))
            {
                string diskText = info.Item2.HasValue ? "Disk " + info.Item2.Value : "Not connected";
                string labelText = string.IsNullOrEmpty(info.Item3) ? "" : "  " + info.Item3;
                string display = $"{diskText} — {info.Item1}:\\{labelText}";

                ProtectedDrives_ListBox.Items.Add(new ListBoxItem
                {
                    Content = display,
                    Tag = new ProtectedListItem { Letter = info.Item1 + ":\\" },
                    ToolTip = display
                });
            }
        }

        private void RemoveProtection_btn_Click(object sender, RoutedEventArgs e)
        {
            if (!(ProtectedDrives_ListBox.SelectedItem is ListBoxItem lbi) || !(lbi.Tag is ProtectedListItem selected))
            {
                MessageBox.Show("Please select a drive from the protected list to remove.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string target = selected.DiskKey != null
                ? $"this disk:\n\n  {lbi.ToolTip}\n\nThe whole disk{(selected.Letters.Count > 0 ? " and its drive(s) " + FormatLetters(selected.Letters) : "")}"
                : $"{selected.Letter}?\n\nThis drive";

            var result = MessageBox.Show(
                $"Are you sure you want to remove protection from {target} will then be eligible for cleaning/formatting.",
                "Remove Protection?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (selected.DiskKey != null)
                {
                    RemoveProtectedDisk(selected.DiskKey);
                    foreach (string letter in selected.Letters)
                        Properties.Settings.Default.ProtectedDrives?.Remove(letter);
                }
                else
                {
                    Properties.Settings.Default.ProtectedDrives?.Remove(selected.Letter);
                }

                Properties.Settings.Default.Save();
                RefreshProtectedDrivesList();
                RefreshDriveDropdowns();
            }
        }

        private void RefreshProtected_btn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Rescan all connected, ready drives and protect every one of them (and their whole
                // disks) so nothing can be cleaned/formatted without the user explicitly unprotecting it first.
                var failures = new List<string>();
                List<string> newlyProtected = ProtectAllConnectedDrives(failures);

                RefreshProtectedDrivesList();
                RefreshDriveDropdowns();

                if (DriveSelection_ddbox.SelectedItem != null)
                {
                    string driveLetter = ExtractDriveLetter(DriveSelection_ddbox.SelectedItem.ToString());
                    UpdateProtectButtonState(driveLetter);
                }

                string failureText = failures.Count > 0
                    ? "\n\nWARNING — not everything could be protected:\n\n" + string.Join("\n", failures.Select(f => "  • " + f))
                    : "";
                MessageBoxImage icon = failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;

                if (newlyProtected.Count > 0)
                {
                    MessageBox.Show($"Rescan complete. Newly protected:\n\n" + string.Join("\n", newlyProtected.Select(l => "  • " + l)) + failureText,
                        "Rescan Complete", MessageBoxButton.OK, icon);
                }
                else
                {
                    MessageBox.Show("Rescan complete. All connected drives are already protected." + failureText,
                        "Rescan Complete", MessageBoxButton.OK, icon);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred while rescanning drives:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Returns every drive letter (e.g. "C:\") that lives on the same physical disk as the given drive letter.
        /// </summary>
        private List<string> GetDriveLettersOnSameDisk(string driveLetter)
        {
            List<string> result = new List<string>();
            string normalizedLetter = driveLetter.Substring(0, 1).ToUpper();

            try
            {
                // Walk letter → partition → physical disk → ALL partitions on that disk → all letters.
                // Without the physical-disk hop the query only ever returns the original letter.
                string partitionQuery = $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{normalizedLetter}:'}} WHERE AssocClass=Win32_LogicalDiskToPartition";
                using (var partitionSearcher = new ManagementObjectSearcher(partitionQuery))
                {
                    foreach (ManagementObject partition in partitionSearcher.Get())
                    {
                        string driveQuery = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition";
                        using (var driveSearcher = new ManagementObjectSearcher(driveQuery))
                        {
                            foreach (ManagementObject physicalDisk in driveSearcher.Get())
                            {
                                foreach (string letter in GetDriveLettersOnDrive(physicalDisk["DeviceID"].ToString()))
                                {
                                    if (!result.Contains(letter))
                                    {
                                        result.Add(letter);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fall back to just the selected drive if WMI lookups fail
            }

            if (result.Count == 0)
            {
                result.Add(normalizedLetter + ":\\");
            }

            return result;
        }

        /// <summary>
        /// Adds a drive to the protected list
        /// </summary>
        private void AddProtectedDrive(string driveLetter)
        {
            if (Properties.Settings.Default.ProtectedDrives == null)
            {
                Properties.Settings.Default.ProtectedDrives = new System.Collections.Specialized.StringCollection();
            }

            if (!Properties.Settings.Default.ProtectedDrives.Contains(driveLetter))
            {
                Properties.Settings.Default.ProtectedDrives.Add(driveLetter);
                Properties.Settings.Default.Save();
                RefreshProtectedDrivesList();
            }
        }

        #region Helper Methods

        /// <summary>
        /// Executes diskpart commands and returns the output
        /// </summary>
        private string ExecuteDiskpartCommand(params string[] commands)
        {
            Process p = new Process();
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.FileName = DISKPART_PATH;
            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.Verb = "runas";
            p.Start();

            foreach (string command in commands)
            {
                p.StandardInput.WriteLine(command);
            }

            p.StandardInput.WriteLine("exit");
            p.StandardInput.Flush();
            p.StandardInput.Close();

            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            return output;
        }

        /// <summary>
        /// Runs a destructive diskpart command only after re-checking the target in the SAME
        /// diskpart session. The first commands select and describe the target; once diskpart has
        /// answered them, their output is passed to verify, and the destructive commands are sent
        /// only if verify returns true. Because the selection is never released in between, a disk
        /// or volume that was unplugged and renumbered can't be swapped in after the check.
        /// </summary>
        private string ExecuteVerifiedDiskpartCommand(string[] selectAndDescribe, Func<string, bool> verify, string[] destructiveCommands, out bool destructiveSent)
        {
            destructiveSent = false;
            var output = new StringBuilder();
            var outputLock = new object();

            using (Process p = new Process())
            {
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.FileName = DISKPART_PATH;
                p.StartInfo.RedirectStandardInput = true;
                p.Start();

                // diskpart's prompt has no newline after it, so read character by character
                var reader = new System.Threading.Thread(() =>
                {
                    int c;
                    while ((c = p.StandardOutput.Read()) >= 0)
                    {
                        lock (outputLock) output.Append((char)c);
                    }
                }) { IsBackground = true };
                reader.Start();

                foreach (string command in selectAndDescribe)
                {
                    p.StandardInput.WriteLine(command);
                }
                p.StandardInput.Flush();

                // diskpart shows a "DISKPART>" prompt at start and after each command, so one more
                // prompt than commands sent means every answer has arrived
                string description = null;
                DateTime deadline = DateTime.Now.AddSeconds(60);
                while (DateTime.Now < deadline && !p.HasExited)
                {
                    string soFar;
                    lock (outputLock) soFar = output.ToString();
                    if (Regex.Matches(soFar, "DISKPART>").Count >= selectAndDescribe.Length + 1)
                    {
                        description = soFar;
                        break;
                    }
                    System.Threading.Thread.Sleep(50);
                }

                if (description != null && !p.HasExited && verify(description))
                {
                    foreach (string command in destructiveCommands)
                    {
                        p.StandardInput.WriteLine(command);
                    }
                    destructiveSent = true;
                }

                try
                {
                    p.StandardInput.WriteLine("exit");
                    p.StandardInput.Flush();
                    p.StandardInput.Close();
                }
                catch (IOException)
                {
                    // diskpart already exited
                }

                if (destructiveSent)
                {
                    // Never interrupt diskpart once a destructive command has been sent
                    p.WaitForExit();
                }
                else if (!p.WaitForExit(30000))
                {
                    // Nothing was changed; a diskpart stuck on a failing disk is safe to stop
                    try { p.Kill(); } catch { }
                }
                reader.Join(5000);
            }

            lock (outputLock) return output.ToString();
        }

        /// <summary>
        /// Removes a temporary drive letter, selecting the volume BY that letter so only the volume
        /// that actually has it is touched. Returns true once the letter is gone.
        /// </summary>
        private bool RemoveTemporaryLetter(string letter)
        {
            try
            {
                string output = ExecuteDiskpartCommand($"select volume {letter}", $"remove letter {letter}");
                CMDOutputBox.AppendText(DeleteLines(output, 11));

                if (!output.Contains("successfully removed the drive letter"))
                    return false;
            }
            catch
            {
                return false;
            }

            // diskpart confirmed it; give Windows a moment to drop the letter too
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (!Directory.GetLogicalDrives().Any(d => char.ToUpper(d[0]) == char.ToUpper(letter[0])))
                    return true;
                System.Threading.Thread.Sleep(300);
            }
            return false;
        }

        /// <summary>
        /// Runs 'list disk', then 'select disk N' + 'detail disk' for every listed disk in one
        /// diskpart session. Returns the 'list disk' output and each disk's parsed details; a
        /// disk whose details could not be read gets an incomplete DiskDetail.
        /// Slow (extra diskpart runs) — call from a background thread.
        /// </summary>
        private Tuple<string, List<DiskListEntry>, Dictionary<int, DiskDetail>> ReadAllDiskDetails()
        {
            string listDiskOutput = ExecuteDiskpartCommand("list disk");
            List<DiskListEntry> disks = DiskpartParser.ParseDiskList(listDiskOutput);
            var details = new Dictionary<int, DiskDetail>();

            if (disks.Count > 0)
            {
                var commands = new List<string>();
                foreach (DiskListEntry disk in disks)
                {
                    commands.Add("select disk " + disk.Number);
                    commands.Add("detail disk");
                }

                // Each section of output starts with "Disk N is now the selected disk."
                Dictionary<int, string> sections = DiskpartParser.SplitDiskSections(ExecuteDiskpartCommand(commands.ToArray()));
                foreach (DiskListEntry disk in disks)
                {
                    sections.TryGetValue(disk.Number, out string section);
                    details[disk.Number] = DiskpartParser.ParseDiskDetail(section, disk.Number);
                }
            }

            return Tuple.Create(listDiskOutput, disks, details);
        }

        /// <summary>
        /// Builds a map of volume number → physical disk number from every disk's 'detail disk'.
        /// </summary>
        private static Dictionary<int, int> BuildVolumeToDiskMap(IEnumerable<DiskDetail> details)
        {
            var map = new Dictionary<int, int>();

            foreach (DiskDetail detail in details)
            {
                foreach (VolumeEntry vol in detail.Volumes)
                {
                    map[vol.Number] = detail.Number;
                }
            }

            return map;
        }

        /// <summary>
        /// Populates the Volumes_dd dropdown from 'diskpart list volume' output with aligned,
        /// readable entries: volume number, disk, letter, filesystem, size and label/info.
        /// FAT32 volumes (EFI partition candidates) are shown in bold.
        /// </summary>
        private void PopulateVolumesDropdown(string diskpartListVolumeOutput)
        {
            _parsedVolumes.Clear();
            Volumes_dd.Items.Clear();

            List<VolumeEntry> entries = DiskpartParser.ParseVolumeTable(diskpartListVolumeOutput);

            // Fallback so the dropdown is never empty if diskpart's layout was unexpected
            if (entries.Count == 0)
            {
                foreach (Match m in Regex.Matches(diskpartListVolumeOutput, @"^\s*Volume\s+(\d+)", RegexOptions.Multiline))
                {
                    entries.Add(new VolumeEntry { Number = int.Parse(m.Groups[1].Value) });
                }
            }

            foreach (VolumeEntry entry in entries)
            {
                if (_volumeToDisk.TryGetValue(entry.Number, out int diskNumber))
                    entry.DiskNumber = diskNumber;

                // diskpart truncates labels to 11 chars ("Websites (S") — get the full
                // label from Windows when the volume has a drive letter
                if (!string.IsNullOrEmpty(entry.Letter))
                {
                    try
                    {
                        var driveInfo = new DriveInfo(entry.Letter + ":\\");
                        if (driveInfo.IsReady && !string.IsNullOrEmpty(driveInfo.VolumeLabel))
                            entry.Label = driveInfo.VolumeLabel;
                    }
                    catch { }
                }

                _parsedVolumes.Add(entry);

                string letterDisplay = string.IsNullOrEmpty(entry.Letter) ? "--" : entry.Letter + ":";
                string diskDisplay = entry.DiskNumber.HasValue ? "Disk " + entry.DiskNumber.Value : "Disk ?";
                string fsDisplay = string.IsNullOrEmpty(entry.FileSystem) ? "-" : entry.FileSystem;

                var tailParts = new List<string>();
                if (!string.IsNullOrEmpty(entry.Label)) tailParts.Add(entry.Label);
                if (!string.IsNullOrEmpty(entry.Info)) tailParts.Add(entry.Info);
                if (!string.IsNullOrEmpty(entry.Status) && entry.Status != "Healthy") tailParts.Add(entry.Status);

                string friendlyText = string.Format("Vol {0,2} │ {1,-6} │ {2,-3}│ {3,-5} │ {4,8} │ {5}",
                    entry.Number, diskDisplay, letterDisplay, fsDisplay, entry.Size, string.Join("  ", tailParts));

                var item = new ComboBoxItem
                {
                    Content = friendlyText,
                    Tag = entry.DiskpartSelector,
                    ToolTip = friendlyText
                };

                if (entry.FileSystem == "FAT32")
                    item.FontWeight = FontWeights.Bold;

                Volumes_dd.Items.Add(item);
            }
        }

        /// <summary>
        /// Returns the disk picked in the Extras tab dropdown, or null (after telling the user)
        /// when nothing valid is selected. Disk numbers only ever come from our own parsed
        /// 'list disk' output, never from free text.
        /// </summary>
        private DiskChoice GetSelectedDiskChoice()
        {
            if (Disk_list_dd.SelectedItem is ComboBoxItem item && item.Tag is DiskChoice choice)
                return choice;

            MessageBox.Show("Please click 'List Disks' and select a disk first.", "No Disk Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        /// <summary>
        /// Looks up a physical disk by its diskpart/Windows disk number through WMI, including
        /// every drive letter on it. Returns null if Windows has no disk with that number.
        /// </summary>
        private PhysicalDisk GetPhysicalDisk(int diskNumber)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_DiskDrive WHERE Index = {diskNumber}"))
                {
                    foreach (ManagementObject drive in searcher.Get())
                    {
                        return BuildPhysicalDisk(drive);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Returns every physical disk Windows currently sees, ordered by disk number. Disks that
        /// could not be read are left out and, if a failures list is given, reported in it.
        /// </summary>
        private List<PhysicalDisk> GetAllPhysicalDisks(List<string> failures = null)
        {
            var disks = new List<PhysicalDisk>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject drive in searcher.Get())
                    {
                        // One unreadable disk (e.g. being unplugged) mustn't hide the others
                        try { disks.Add(BuildPhysicalDisk(drive)); }
                        catch { failures?.Add($"A disk ({Convert.ToString(drive["Model"])}) could not be read by Windows, so it was NOT protected. Run 'Rescan & Protect Drives' again."); }
                    }
                }
            }
            catch
            {
                failures?.Add("Windows could not list the disks, so they were NOT protected. Run 'Rescan & Protect Drives' again.");
            }
            return disks.OrderBy(d => d.Number).ToList();
        }

        /// <summary>
        /// Returns the physical disk a drive letter lives on, or null for network drives and
        /// letters that aren't on a physical disk.
        /// </summary>
        private PhysicalDisk GetPhysicalDiskForLetter(string driveLetter)
        {
            if (string.IsNullOrEmpty(driveLetter))
                return null;

            int? diskNumber = GetDiskNumberForLetter(driveLetter.Substring(0, 1).ToUpper());
            return diskNumber.HasValue ? GetPhysicalDisk(diskNumber.Value) : null;
        }

        private PhysicalDisk BuildPhysicalDisk(ManagementObject drive)
        {
            var disk = new PhysicalDisk
            {
                Number = Convert.ToInt32(drive["Index"]),
                Model = Convert.ToString(drive["Model"]).Trim(),
                SerialNumber = Convert.ToString(drive["SerialNumber"]).Trim(),
                SizeBytes = drive["Size"] != null ? Convert.ToUInt64(drive["Size"]) : 0
            };
            disk.Key = DiskpartParser.BuildDiskKey(disk.Model, disk.SerialNumber, disk.SizeBytes, Convert.ToString(drive["PNPDeviceID"]));
            disk.Letters = GetDriveLettersOnDrive(Convert.ToString(drive["DeviceID"]));
            return disk;
        }

        /// <summary>
        /// A short name for a disk that the user can recognise, e.g. "Samsung SSD 870 EVO (465.76 GB)".
        /// </summary>
        private string DescribeDisk(PhysicalDisk disk)
        {
            string model = disk.Model.Length > 0 ? disk.Model : "Unknown disk";
            return $"{model} ({FormatBytes((long)disk.SizeBytes)})";
        }

        /// <summary>
        /// Returns every drive letter (e.g. "C:\") on a physical disk, given its WMI DeviceID
        /// (e.g. "\\.\PHYSICALDRIVE1"). Uses WMI rather than diskpart text, so it works
        /// regardless of the Windows display language.
        /// </summary>
        private List<string> GetDriveLettersOnDrive(string diskDeviceId)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(diskDeviceId))
                return result;

            try
            {
                AddDriveLettersOnDrive(diskDeviceId, result);
            }
            catch
            {
                // Partial result; Clean/Convert compare it with diskpart's list and refuse on a mismatch
            }

            return result;
        }

        private static void AddDriveLettersOnDrive(string diskDeviceId, List<string> result)
        {
            string escapedDiskId = diskDeviceId.Replace(@"\", @"\\");
            string allPartitionsQuery = $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{escapedDiskId}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition";
            using (var allPartitionsSearcher = new ManagementObjectSearcher(allPartitionsQuery))
            {
                foreach (ManagementObject diskPartition in allPartitionsSearcher.Get())
                {
                    string logicalQuery = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{diskPartition["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition";
                    using (var logicalSearcher = new ManagementObjectSearcher(logicalQuery))
                    {
                        foreach (ManagementObject logicalDisk in logicalSearcher.Get())
                        {
                            string letter = logicalDisk["DeviceID"].ToString().ToUpper() + "\\";
                            if (!result.Contains(letter))
                                result.Add(letter);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// True if the drive letter is the one the running copy of Windows lives on.
        /// </summary>
        private static bool IsHostSystemLetter(string driveLetter)
        {
            string systemRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
            return !string.IsNullOrEmpty(driveLetter) && !string.IsNullOrEmpty(systemRoot)
                && char.ToUpper(driveLetter[0]) == char.ToUpper(systemRoot[0]);
        }

        /// <summary>
        /// Checks a disk before Clean or Convert. Fails closed: if the disk cannot be found, or
        /// diskpart's details cannot be fully read, the disk is treated as blocked rather than
        /// unprotected. The disk running Windows is always blocked, protected list or not.
        /// Letters come from both WMI and diskpart so a single bad reading can't hide one.
        /// </summary>
        private DiskSafetyCheck InspectDisk(int diskNumber)
        {
            var check = new DiskSafetyCheck { Number = diskNumber };

            check.Disk = GetPhysicalDisk(diskNumber);
            string detailOutput = ExecuteDiskpartCommand($"select disk {diskNumber}", "detail disk");
            check.Detail = DiskpartParser.ParseDiskDetail(detailOutput, diskNumber);

            check.Letters = (check.Disk != null ? check.Disk.Letters : new List<string>())
                .Union(check.Detail.Letters)
                .Distinct()
                .OrderBy(l => l)
                .ToList();

            if (check.Disk == null || string.IsNullOrEmpty(check.Disk.Key))
                check.BlockReasons.Add($"Windows could not identify Disk {diskNumber}. It may have been unplugged or renumbered — click 'List Disks' again.");

            // Two connected disks with the same identity (some docks report one serial for every
            // bay) can't be told apart, so neither can be safely targeted
            if (check.Disk != null && !string.IsNullOrEmpty(check.Disk.Key)
                && GetAllPhysicalDisks().Count(d => d.Key == check.Disk.Key) > 1)
                check.BlockReasons.Add($"Another connected disk reports exactly the same model, serial number and size as Disk {diskNumber}, so they can't be told apart. Disconnect the other disk first.");

            if (!check.Detail.IsComplete)
                check.BlockReasons.Add($"diskpart did not return complete details for Disk {diskNumber}, so its protection cannot be verified."
                    + (check.Detail.Selected ? "" : " (This tool reads diskpart's English output — on other Windows display languages Clean and Convert are not available.)"));

            // Windows and diskpart must agree on which disk this number is: every letter Windows
            // puts on it must be on diskpart's list too, and no letter diskpart lists may belong to
            // another disk. (Windows can't map some letters, e.g. on dynamic disks — that's fine.)
            if (check.Disk != null && check.Detail.IsComplete)
            {
                bool windowsLetterMissing = check.Disk.Letters.Any(l => !check.Detail.Letters.Contains(l, StringComparer.OrdinalIgnoreCase));
                bool letterOnOtherDisk = check.Detail.Letters.Any(l =>
                {
                    int? otherDisk = GetDiskNumberForLetter(l.Substring(0, 1));
                    return otherDisk.HasValue && otherDisk.Value != diskNumber;
                });

                if (windowsLetterMissing || letterOnOtherDisk)
                    check.BlockReasons.Add($"Windows lists drive letters [{FormatLetters(check.Disk.Letters)}] on Disk {diskNumber}, but diskpart lists [{FormatLetters(check.Detail.Letters)}]. They do not agree on which disk this is.");
            }

            if (check.Detail.IsInUseByWindows || check.Letters.Any(IsHostSystemLetter))
                check.BlockReasons.Add($"Disk {diskNumber} holds the copy of Windows this PC is running from.");

            string protectionMatch = FindProtectionMatch(check.Disk, check.Detail.DiskId);
            if (protectionMatch != null)
                check.BlockReasons.Add(protectionMatch);

            List<string> protectedLetters = check.Letters.Where(IsLetterInProtectedList).ToList();
            if (protectedLetters.Count > 0)
                check.BlockReasons.Add("It contains protected drive(s): " + string.Join("  ", protectedLetters));

            return check;
        }

        /// <summary>
        /// Builds a human-readable description of a disk for confirmation dialogs: model, size,
        /// serial and every volume on it, so the user can see exactly what is about to be erased.
        /// </summary>
        private string FormatDiskSummary(DiskSafetyCheck check)
        {
            StringBuilder summary = new StringBuilder();

            string model = check.Disk != null && check.Disk.Model.Length > 0 ? check.Disk.Model : check.Detail.Model;
            summary.AppendLine($"Disk {check.Number} — {(string.IsNullOrEmpty(model) ? "unknown model" : model)}");
            if (check.Disk != null)
            {
                summary.AppendLine($"Size:    {FormatBytes((long)check.Disk.SizeBytes)}");
                summary.AppendLine($"Serial:  {(check.Disk.SerialNumber.Length > 0 ? check.Disk.SerialNumber : "unknown")}");
            }

            summary.AppendLine();
            summary.AppendLine("Volumes on this disk:");
            if (check.Detail.Volumes.Count == 0)
            {
                summary.AppendLine("  (none)");
            }
            foreach (VolumeEntry volume in check.Detail.Volumes)
            {
                string label = volume.Label;
                if (!string.IsNullOrEmpty(volume.Letter))
                {
                    try
                    {
                        var driveInfo = new DriveInfo(volume.Letter + ":\\");
                        if (driveInfo.IsReady && !string.IsNullOrEmpty(driveInfo.VolumeLabel))
                            label = driveInfo.VolumeLabel;
                    }
                    catch { }
                }

                string letterDisplay = string.IsNullOrEmpty(volume.Letter) ? "--" : volume.Letter + ":";
                summary.AppendLine($"  {letterDisplay}  {(string.IsNullOrEmpty(label) ? "(no label)" : label)}  {volume.FileSystem}  {volume.Size}  {volume.Info}".TrimEnd());
            }

            // Make it obvious when the drive picked on the main screen is (or isn't) on this disk
            if (DriveSelection_ddbox.SelectedItem != null)
            {
                string mainDrive = ExtractDriveLetter(DriveSelection_ddbox.SelectedItem.ToString()).ToUpper() + ":\\";
                summary.AppendLine();
                summary.AppendLine(check.Letters.Contains(mainDrive)
                    ? $"The drive selected on the main screen ({mainDrive}) IS on this disk."
                    : $"Note: the drive selected on the main screen ({mainDrive}) is NOT on this disk.");
            }

            return summary.ToString();
        }

        private static bool SameLetters(IEnumerable<string> a, IEnumerable<string> b)
        {
            return new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(new HashSet<string>(b, StringComparer.OrdinalIgnoreCase));
        }

        private static string FormatLetters(IEnumerable<string> letters)
        {
            var list = letters.Select(l => l.TrimEnd('\\')).OrderBy(l => l).ToList();
            return list.Count > 0 ? string.Join(" ", list) : "none";
        }

        /// <summary>
        /// Runs the safety check for a destructive operation and shows why it is refused, if it
        /// is. The disk number, hardware identity, Disk ID and drive letters must all still be
        /// what they were when the disk was chosen — if any changed (e.g. a USB drive was
        /// plugged in or removed and the numbers shifted), the operation is refused.
        /// Returns the check if the operation may go ahead, otherwise null.
        /// </summary>
        private DiskSafetyCheck CheckDiskBeforeDestructiveOperation(DiskChoice choice, string operationName)
        {
            DiskSafetyCheck check = InspectDisk(choice.Number);

            var changes = new List<string>();
            if (check.Disk != null && check.Disk.Key != choice.Key)
                changes.Add("its hardware (model / serial number / size)");
            if (check.Detail.IsComplete && !string.Equals(check.Detail.DiskId, choice.DiskId, StringComparison.OrdinalIgnoreCase))
                changes.Add($"its Disk ID ({(choice.DiskId.Length > 0 ? choice.DiskId : "none")} → {(check.Detail.DiskId.Length > 0 ? check.Detail.DiskId : "none")})");
            if (!SameLetters(check.Letters, choice.Letters))
                changes.Add($"its drive letters ([{FormatLetters(choice.Letters)}] → [{FormatLetters(check.Letters)}])");

            if (changes.Count > 0)
                check.BlockReasons.Insert(0, $"Disk {choice.Number} is no longer the disk that was listed — {string.Join(", ", changes)} changed. Click 'List Disks' again and re-check your selection.");

            if (!check.IsBlocked)
                return check;

            StringBuilder message = new StringBuilder();
            message.AppendLine($"CANNOT {operationName.ToUpper()} Disk {choice.Number}!");
            message.AppendLine();
            foreach (string reason in check.BlockReasons)
            {
                message.AppendLine($"  • {reason}");
            }
            message.AppendLine();
            message.AppendLine("Nothing was changed. You can manage protected drives in the Extras tab.");

            MessageBox.Show(message.ToString(), $"Protected Disk - Cannot {operationName}", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        /// <summary>
        /// Repeats the safety check immediately before running the operation, after the user has
        /// clicked through the confirmations, and makes sure it is still the very same disk.
        /// Returns the fresh check, or null if the operation must not run.
        /// </summary>
        private DiskSafetyCheck RecheckDiskJustBeforeRunning(DiskChoice choice, DiskSafetyCheck confirmed, string operationName)
        {
            var confirmedChoice = new DiskChoice
            {
                Number = choice.Number,
                Key = confirmed.Disk.Key,
                DiskId = confirmed.Detail.DiskId,
                Letters = confirmed.Letters
            };
            return CheckDiskBeforeDestructiveOperation(confirmedChoice, operationName);
        }

        /// <summary>
        /// Re-reads the disks right before Fix Boot changes anything and makes sure everything
        /// still pairs up the way it did at 'Refresh': the EFI volume number still has the same
        /// size and file system and is still on the same disk number, that disk still has the
        /// same hardware identity, Disk ID and drive letters, the Windows drive letter is still
        /// on its disk and still holds Windows, and the temporary letter is still free.
        /// Returns the problems found; an empty list means it is safe to continue.
        /// </summary>
        private List<string> VerifyFixBootTargets(VolumeEntry efiVolume, string windowsLetter, int? windowsDisk, string tempLetter)
        {
            var problems = new List<string>();
            int diskNumber = efiVolume.DiskNumber.Value;

            // The volume number must still point at the same volume...
            VolumeEntry currentVolume = DiskpartParser.ParseVolumeTable(ExecuteDiskpartCommand("list vol"))
                .FirstOrDefault(v => v.Number == efiVolume.Number);
            if (currentVolume == null || currentVolume.Size != efiVolume.Size
                || currentVolume.FileSystem != efiVolume.FileSystem || currentVolume.Letter != efiVolume.Letter)
                problems.Add($"{efiVolume.DiskpartSelector} is no longer the {efiVolume.FileSystem} {efiVolume.Size} volume that was listed.");

            // ...on the same physical disk
            DiskDetail detail = DiskpartParser.ParseDiskDetail(ExecuteDiskpartCommand($"select disk {diskNumber}", "detail disk"), diskNumber);
            PhysicalDisk disk = GetPhysicalDisk(diskNumber);
            _diskDetails.TryGetValue(diskNumber, out DiskDetail listedDetail);
            _physicalDisks.TryGetValue(diskNumber, out PhysicalDisk listedDisk);

            if (!detail.IsComplete)
                problems.Add($"diskpart did not return complete details for Disk {diskNumber}.");
            else if (!detail.Volumes.Any(v => v.Number == efiVolume.Number && v.Size == efiVolume.Size && v.FileSystem == efiVolume.FileSystem))
                problems.Add($"{efiVolume.DiskpartSelector} is no longer on Disk {diskNumber}.");

            if (listedDetail == null || !listedDetail.IsComplete
                || !string.Equals(detail.DiskId, listedDetail.DiskId, StringComparison.OrdinalIgnoreCase))
                problems.Add($"Disk {diskNumber}'s Disk ID is not the one seen at 'Refresh'.");

            if (disk == null || listedDisk == null || string.IsNullOrEmpty(disk.Key) || disk.Key != listedDisk.Key)
                problems.Add($"Disk {diskNumber} is not the same hardware (model / serial number / size) it was at 'Refresh'.");
            else if (!SameLetters(disk.Letters, listedDisk.Letters))
                problems.Add($"Disk {diskNumber}'s drive letters changed ([{FormatLetters(listedDisk.Letters)}] → [{FormatLetters(disk.Letters)}]).");

            if (disk != null && detail.IsComplete && !SameLetters(disk.Letters, detail.Letters))
                problems.Add($"Windows and diskpart do not agree on which drive letters are on Disk {diskNumber}.");

            // The Windows drive must still be the same Windows installation on the same disk
            if (!IsWindowsInstallation(windowsLetter + ":\\"))
                problems.Add($"{windowsLetter}:\\ does not hold a Windows installation.");

            int? currentWindowsDisk = GetDiskNumberForLetter(windowsLetter);
            if (!windowsDisk.HasValue || currentWindowsDisk != windowsDisk)
            {
                problems.Add($"Drive {windowsLetter}: could not be matched to the disk it was on at 'Refresh'.");
            }
            else
            {
                // The Windows disk gets the same pairing as the EFI disk: same hardware, same letters
                PhysicalDisk windowsPhysical = GetPhysicalDisk(windowsDisk.Value);
                _physicalDisks.TryGetValue(windowsDisk.Value, out PhysicalDisk listedWindowsDisk);
                if (windowsPhysical == null || listedWindowsDisk == null || string.IsNullOrEmpty(windowsPhysical.Key)
                    || windowsPhysical.Key != listedWindowsDisk.Key
                    || !windowsPhysical.Letters.Contains(windowsLetter + ":\\")
                    || !SameLetters(windowsPhysical.Letters, listedWindowsDisk.Letters))
                    problems.Add($"Disk {windowsDisk} (holding {windowsLetter}:) is not the same disk it was at 'Refresh'.");

                // ...and the same Disk ID (already checked above when it's the EFI volume's disk)
                if (windowsDisk.Value != diskNumber)
                {
                    DiskDetail windowsDetail = DiskpartParser.ParseDiskDetail(ExecuteDiskpartCommand($"select disk {windowsDisk.Value}", "detail disk"), windowsDisk.Value);
                    _diskDetails.TryGetValue(windowsDisk.Value, out DiskDetail listedWindowsDetail);
                    if (!windowsDetail.IsComplete || listedWindowsDetail == null
                        || !string.Equals(windowsDetail.DiskId, listedWindowsDetail.DiskId, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"Disk {windowsDisk}'s Disk ID is not the one seen at 'Refresh'.");
                }
            }

            // And the temporary letter must still be free (including mapped network drives)
            if (GetLettersInUse().Contains(char.ToUpper(tempLetter[0])))
                problems.Add($"The temporary letter {tempLetter}: is already in use.");

            return problems;
        }

        /// <summary>
        /// Waits briefly for a newly assigned drive letter to show up in Windows and returns true
        /// only if it sits on the expected physical disk — same disk number AND same hardware.
        /// </summary>
        private bool WaitForLetterOnDisk(string letter, int expectedDisk, PhysicalDisk expectedHardware)
        {
            if (expectedHardware == null || string.IsNullOrEmpty(expectedHardware.Key))
                return false;

            for (int attempt = 0; attempt < 10; attempt++)
            {
                int? disk = GetDiskNumberForLetter(letter);
                if (disk.HasValue)
                {
                    PhysicalDisk actual = GetPhysicalDisk(disk.Value);
                    return disk.Value == expectedDisk && actual != null && actual.Key == expectedHardware.Key;
                }

                System.Threading.Thread.Sleep(300);
            }
            return false;
        }

        /// <summary>
        /// Gets the diskpart selector (e.g. "Volume 2") for whatever is selected in Volumes_dd,
        /// whether it's a friendly ComboBoxItem or a plain string.
        /// </summary>
        private string GetSelectedVolumeSelector()
        {
            if (Volumes_dd.SelectedItem is ComboBoxItem item && item.Tag != null)
                return item.Tag.ToString();

            return Volumes_dd.SelectedItem?.ToString();
        }

        /// <summary>
        /// Automatically selects the FAT32 (EFI) volume that belongs to the currently selected
        /// cloned drive. When the volume→disk map is available it picks a FAT32 volume on the
        /// same physical disk; otherwise it falls back to the next FAT32 volume after the drive's
        /// own row in the diskpart listing. Volumes marked 'System' (the EFI partition the
        /// current PC boots from) are avoided. The user can still change the selection afterward.
        /// </summary>
        private void AutoSelectFat32Volume()
        {
            if (DriveSelection_ddbox.SelectedItem == null || _parsedVolumes.Count == 0)
                return;

            string selectedDriveLetter = ExtractDriveLetter(DriveSelection_ddbox.SelectedItem.ToString()).ToUpper();
            VolumeEntry driveVolume = _parsedVolumes.FirstOrDefault(v => v.Letter == selectedDriveLetter);

            // Only a letterless FAT32 volume that isn't the running PC's own EFI partition (or on
            // the disk Windows is running from) can be a cloned drive's EFI partition
            bool IsCandidateEfi(VolumeEntry v) =>
                v.FileSystem == "FAT32"
                && string.IsNullOrEmpty(v.Letter)
                && !Regex.IsMatch(v.Info, @"\b(System|Boot)\b", RegexOptions.IgnoreCase)
                && !(v.DiskNumber.HasValue && _diskDetails.TryGetValue(v.DiskNumber.Value, out DiskDetail d) && d.IsInUseByWindows);

            VolumeEntry fat32Match = null;

            // Best: the EFI volume paired with the selected drive — on the same physical disk,
            // i.e. the disk with the same Disk ID as the one holding the Windows drive
            if (driveVolume != null && driveVolume.DiskNumber.HasValue)
            {
                fat32Match = _parsedVolumes.FirstOrDefault(v => IsCandidateEfi(v) && v.DiskNumber == driveVolume.DiskNumber);
            }

            // Next best: the FAT32 volume following the drive's own row (typical clone layout),
            // only when it is not known to be on another disk
            if (fat32Match == null && driveVolume != null)
            {
                int startIndex = _parsedVolumes.IndexOf(driveVolume);
                fat32Match = _parsedVolumes.Skip(startIndex + 1).FirstOrDefault(v => IsCandidateEfi(v)
                    && (!v.DiskNumber.HasValue || !driveVolume.DiskNumber.HasValue || v.DiskNumber == driveVolume.DiskNumber));
            }

            // Clear any previous recommendation highlight before applying a new one
            foreach (var obj in Volumes_dd.Items)
            {
                if (obj is ComboBoxItem previous)
                    previous.ClearValue(BackgroundProperty);
            }

            if (fat32Match == null)
                return;

            foreach (var obj in Volumes_dd.Items)
            {
                if (obj is ComboBoxItem cbi && cbi.Tag?.ToString() == fat32Match.DiskpartSelector)
                {
                    // Green highlight marks the recommended EFI volume for the chosen drive;
                    // it stays visible in the list even if the user picks a different one
                    cbi.Background = new SolidColorBrush(Color.FromRgb(0xC9, 0xF0, 0xC9));
                    Volumes_dd.SelectedItem = cbi;
                    break;
                }
            }
        }

        /// <summary>
        /// Checks if a drive is protected, either by its letter or because the physical disk
        /// it lives on is protected. The disk check keeps a disk protected when its letters change.
        /// </summary>
        private bool IsDriveProtected(string driveLetter)
        {
            if (IsLetterInProtectedList(driveLetter))
                return true;

            PhysicalDisk disk = GetPhysicalDiskForLetter(driveLetter);
            return IsDiskProtected(disk);
        }

        /// <summary>
        /// Checks if a drive letter itself is in the protected list
        /// </summary>
        private bool IsLetterInProtectedList(string driveLetter)
        {
            if (Properties.Settings.Default.ProtectedDrives == null || string.IsNullOrEmpty(driveLetter))
                return false;

            string normalizedLetter = driveLetter.Substring(0, 1).ToUpper();

            foreach (string protectedDrive in Properties.Settings.Default.ProtectedDrives)
            {
                if (protectedDrive.StartsWith(normalizedLetter, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the physical disks in the protected list.
        /// </summary>
        private List<ProtectedDiskEntry> GetProtectedDisks()
        {
            if (Properties.Settings.Default.ProtectedDisks == null)
                return new List<ProtectedDiskEntry>();

            return Properties.Settings.Default.ProtectedDisks
                .Cast<string>()
                .Select(ProtectedDiskEntry.Parse)
                .Where(entry => entry.Key.Length > 0)
                .ToList();
        }

        /// <summary>
        /// Checks if a physical disk (by hardware key) is in the protected list
        /// </summary>
        private bool IsDiskProtected(string diskKey)
        {
            return !string.IsNullOrEmpty(diskKey) && GetProtectedDisks().Any(entry => entry.Key == diskKey);
        }

        /// <summary>
        /// Checks if a physical disk is protected, also recognising a protected disk whose model
        /// name changed because it moved to another connection (e.g. from a USB dock to SATA):
        /// the same serial number and size still count as the same disk.
        /// </summary>
        private bool IsDiskProtected(PhysicalDisk disk)
        {
            return disk != null && FindProtectionMatch(disk, null) != null;
        }

        /// <summary>
        /// Returns why a disk counts as protected, or null if it doesn't. Matches the exact
        /// hardware key, or the same serial number and size (the same disk on another connection).
        /// </summary>
        private string FindProtectionMatch(PhysicalDisk disk, string diskId)
        {
            if (disk == null || string.IsNullOrEmpty(disk.Key))
                return null;

            List<ProtectedDiskEntry> entries = GetProtectedDisks();

            if (entries.Any(entry => entry.Key == disk.Key))
                return $"Disk {disk.Number} ({DescribeDisk(disk)}) is on the protected list.";

            ProtectedDiskEntry sameSerial = entries.FirstOrDefault(entry =>
                disk.SerialNumber.Length > 0 && entry.SerialNumber == disk.SerialNumber && entry.SizeBytes == disk.SizeBytes);
            if (sameSerial != null)
                return $"Disk {disk.Number} has the same serial number and size as protected disk {sameSerial.Name} — it is that disk on another connection.";

            // The Disk ID is deliberately not used here: a clone copies it, so it would tie the
            // clone to its protected source. It is only used to pair a disk between checks.
            return null;
        }

        /// <summary>
        /// Adds a physical disk to the protected list. Returns true if it was newly added.
        /// </summary>
        private bool AddProtectedDisk(PhysicalDisk disk, string diskId = "")
        {
            if (disk == null || string.IsNullOrEmpty(disk.Key) || IsDiskProtected(disk.Key))
                return false;

            if (Properties.Settings.Default.ProtectedDisks == null)
                Properties.Settings.Default.ProtectedDisks = new System.Collections.Specialized.StringCollection();

            Properties.Settings.Default.ProtectedDisks.Add(new ProtectedDiskEntry
            {
                Key = disk.Key,
                DiskId = diskId ?? "",
                Name = DescribeDisk(disk)
            }.ToString());
            Properties.Settings.Default.Save();
            return true;
        }

        /// <summary>
        /// Removes a physical disk (by hardware key) from the protected list.
        /// </summary>
        private void RemoveProtectedDisk(string diskKey)
        {
            if (Properties.Settings.Default.ProtectedDisks == null || string.IsNullOrEmpty(diskKey))
                return;

            foreach (string stored in Properties.Settings.Default.ProtectedDisks.Cast<string>().ToList())
            {
                if (ProtectedDiskEntry.Parse(stored).Key == diskKey)
                    Properties.Settings.Default.ProtectedDisks.Remove(stored);
            }
            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// Protects every connected local disk — every physical disk Windows sees, including disks
        /// with no drive letter, offline, RAW or BitLocker-locked ones — together with all of its
        /// drive letters, so nothing can be cleaned or formatted until the user explicitly
        /// unprotects it. Used on first run and by 'Rescan &amp; Protect Drives'.
        /// Returns a line for each newly protected disk; anything that could not be protected
        /// (no disks found, or a disk that can't be identified) is added to failures.
        /// </summary>
        private const string NoDisksFoundMessage = "Windows did not report any disks, so nothing could be protected. Run 'Rescan & Protect Drives' again.";

        private List<string> ProtectAllConnectedDrives(List<string> failures)
        {
            var newlyProtected = new List<string>();

            if (Properties.Settings.Default.ProtectedDrives == null)
                Properties.Settings.Default.ProtectedDrives = new System.Collections.Specialized.StringCollection();

            List<PhysicalDisk> disks = GetAllPhysicalDisks(failures);
            if (disks.Count == 0)
                failures.Add(NoDisksFoundMessage);

            foreach (PhysicalDisk disk in disks)
            {
                var addedLetters = disk.Letters.Where(l => !Properties.Settings.Default.ProtectedDrives.Contains(l)).ToList();
                foreach (string letter in addedLetters)
                    Properties.Settings.Default.ProtectedDrives.Add(letter);

                // A disk without an identity can't be listed, but Clean/Convert always refuse it
                if (string.IsNullOrEmpty(disk.Key))
                {
                    newlyProtected.Add($"Disk {disk.Number} — {DescribeDisk(disk)}  (no serial number — Clean/Convert always refuse it)");
                    continue;
                }

                if (AddProtectedDisk(disk) || addedLetters.Count > 0)
                    newlyProtected.Add($"Disk {disk.Number} — {DescribeDisk(disk)}  [{FormatLetters(disk.Letters)}]");
            }

            // Local letters that Windows could not place on any disk are protected by letter
            var lettersOnDisks = new HashSet<string>(disks.SelectMany(d => d.Letters), StringComparer.OrdinalIgnoreCase);
            foreach (var drive in Environment.GetLogicalDrives())
            {
                try
                {
                    DriveInfo driveInfo = new DriveInfo(drive);
                    if (!IsLocalDrive(driveInfo) || lettersOnDisks.Contains(drive.ToUpper()))
                        continue;

                    if (!Properties.Settings.Default.ProtectedDrives.Contains(drive.ToUpper()))
                    {
                        Properties.Settings.Default.ProtectedDrives.Add(drive.ToUpper());
                        newlyProtected.Add(drive.ToUpper());
                    }
                }
                catch { }
            }

            Properties.Settings.Default.Save();
            return newlyProtected;
        }

        /// <summary>
        /// After this tool has cleaned or converted a disk, its Disk ID and letters change. Update
        /// the remembered pairing for that disk so the next step (e.g. Convert after Clean) checks
        /// against the disk as it is now — but only if it is still the same hardware.
        /// </summary>
        private void UpdateDiskChoiceAfterChange(DiskChoice choice, string expectedKey)
        {
            try
            {
                DiskSafetyCheck current = InspectDisk(choice.Number);

                if (Disk_list_dd.SelectedItem is ComboBoxItem item && item.Tag == choice)
                {
                    if (current.Disk != null && current.Disk.Key == expectedKey && current.Detail.IsComplete)
                    {
                        item.Tag = new DiskChoice
                        {
                            Number = choice.Number,
                            Key = expectedKey,
                            DiskId = current.Detail.DiskId,
                            Letters = current.Letters
                        };
                    }
                    else
                    {
                        Disk_list_dd.Items.Clear();
                    }
                }
            }
            catch
            {
                Disk_list_dd.Items.Clear();
            }
        }

        /// <summary>
        /// One-time upgrade for lists saved by older versions, which protected drive letters only:
        /// also protect the physical disk each of those letters is on now, so the protection stays
        /// with the disk even if its letters change later.
        /// </summary>
        private void ProtectDisksOfProtectedLetters()
        {
            if (Properties.Settings.Default.ProtectedDisks == null)
                Properties.Settings.Default.ProtectedDisks = new System.Collections.Specialized.StringCollection();

            if (Properties.Settings.Default.ProtectedDrives != null)
            {
                foreach (string drive in Properties.Settings.Default.ProtectedDrives.Cast<string>().ToList())
                {
                    try
                    {
                        AddProtectedDisk(GetPhysicalDiskForLetter(drive));
                    }
                    catch { }
                }
            }

            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// First run: protects every connected drive and its whole physical disk (the same as
        /// 'Rescan &amp; Protect Drives'), THEN tells the user exactly what was protected.
        /// Returns false if anything could not be protected, so first run is tried again next launch.
        /// </summary>
        private bool ShowFirstRunDialog()
        {
            // Protect first, so the dialog only ever reports what really happened
            ProtectDisksOfProtectedLetters();
            var failures = new List<string>();
            List<string> newlyProtected = ProtectAllConnectedDrives(failures);

            StringBuilder message = new StringBuilder();
            message.AppendLine("Welcome to Cloned GPT Drive - Boot Fix!");
            message.AppendLine();
            message.AppendLine("For your safety, every connected drive and the whole disk it is on is now protected:");
            message.AppendLine();
            foreach (string line in newlyProtected)
            {
                message.AppendLine($"  • {line}");
            }
            if (newlyProtected.Count == 0)
            {
                message.AppendLine("  (no drives were found)");
            }
            if (failures.Count > 0)
            {
                message.AppendLine();
                message.AppendLine("WARNING — not everything could be protected:");
                foreach (string failure in failures)
                {
                    message.AppendLine($"  • {failure}");
                }
            }
            message.AppendLine();
            message.AppendLine("Protected disks cannot be cleaned or converted through this tool.");
            message.AppendLine("Drives connected later are not protected until you click 'Rescan & Protect Drives'.");
            message.AppendLine("To work on a disk, unprotect it on the main screen or in the Extras tab.");

            MessageBox.Show(message.ToString(), "First Run - Drive Protection", MessageBoxButton.OK,
                failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            // Try again next launch if any disk could not be read or protected
            return failures.Count == 0;
        }

        /// <summary>
        /// Formats bytes to human-readable format
        /// </summary>
        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        /// <summary>
        /// Shows a confirmation dialog before destructive operations
        /// </summary>
        private bool ConfirmDestructiveOperation(string diskInfo, string operation)
        {
            string message = $"WARNING: You are about to {operation}:\n\n{diskInfo}\n\nThis operation will DESTROY ALL DATA on this disk.\n\nAre you absolutely sure?";
            return MessageBox.Show(message, "Confirm Destructive Operation", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        #endregion

        public static string DeleteLines(string s, int linesToRemove)
        {
            return s.Split(Environment.NewLine.ToCharArray(),
                           linesToRemove + 1
                ).Skip(linesToRemove)
                .FirstOrDefault();
        }

        private void FixBoot_btn_Click(object sender, RoutedEventArgs e)
        {
            // Validate selections
            if (DriveSelection_ddbox.SelectedItem == null)
            {
                MessageBox.Show("Please select the cloned drive with Windows first.", "No Drive Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string volumeSelector = GetSelectedVolumeSelector();
            if (string.IsNullOrEmpty(volumeSelector))
            {
                MessageBox.Show("Please select the FAT32 volume first.\n\nClick 'Refresh' to load the volume list.", "No Volume Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Never pass anything but a clean "Volume N" selector to the elevated diskpart session
            if (!Regex.IsMatch(volumeSelector, @"^Volume \d+$"))
            {
                MessageBox.Show("The selected volume entry is not valid. Click 'Refresh' and select the volume again.",
                    "Invalid Volume Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (UnusedDriveSelection_ddbox.SelectedItem == null)
            {
                MessageBox.Show("Please select an unused drive letter.", "No Letter Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Sanity-check the chosen volume before touching anything. A volume that isn't in the
            // last Refresh can't be paired with its disk, so it can't be verified either
            VolumeEntry selectedVolume = _parsedVolumes.FirstOrDefault(v => v.DiskpartSelector == volumeSelector);
            if (selectedVolume == null || !selectedVolume.DiskNumber.HasValue)
            {
                MessageBox.Show("The disk that the selected volume is on could not be determined, so the selection cannot be verified.\n\nClick 'Refresh' and select the volume again.",
                    "Cannot Verify Volume", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Never rewrite the boot files of the PC that is running this tool: its EFI partition
            // is marked System/Boot, and the disk it is on is in use by Windows
            _diskDetails.TryGetValue(selectedVolume.DiskNumber.Value, out DiskDetail efiDiskDetail);
            if (Regex.IsMatch(selectedVolume.Info, @"\b(System|Boot)\b", RegexOptions.IgnoreCase)
                || efiDiskDetail == null || efiDiskDetail.IsInUseByWindows)
            {
                MessageBox.Show(efiDiskDetail == null
                        ? "The details of the disk the selected volume is on could not be read, so it cannot be verified.\n\nClick 'Refresh' and try again."
                        : "The selected volume is on the disk your CURRENT PC boots and runs Windows from (it is marked System/Boot).\n\nFix Boot only repairs the EFI partition of a cloned drive. Select the FAT32 volume on the cloned drive instead.",
                    "Cannot Use This Volume", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // An EFI partition has no drive letter. A volume that has one is a data partition, and
            // the temporary letter would replace (and then remove) its letter for good
            if (!string.IsNullOrEmpty(selectedVolume.Letter))
            {
                MessageBox.Show($"The selected volume already has drive letter {selectedVolume.Letter}:, so it is not an EFI partition.\n\nSelect the cloned drive's FAT32 EFI volume (it has no drive letter).",
                    "Cannot Use This Volume", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string windowsLetter = ExtractDriveLetter(DriveSelection_ddbox.SelectedItem.ToString()).ToUpper();
            VolumeEntry windowsVolume = _parsedVolumes.FirstOrDefault(v => v.Letter == windowsLetter);
            int? windowsDisk = windowsVolume != null && windowsVolume.DiskNumber.HasValue
                ? windowsVolume.DiskNumber
                : GetDiskNumberForLetter(windowsLetter);

            {
                var warnings = new List<string>();

                if (selectedVolume.FileSystem != "FAT32")
                    warnings.Add($"• The selected volume is {(string.IsNullOrEmpty(selectedVolume.FileSystem) ? "not FAT32" : selectedVolume.FileSystem)} — the EFI partition is normally FAT32.");

                if (windowsDisk.HasValue && selectedVolume.DiskNumber != windowsDisk)
                    warnings.Add($"• The selected volume is on Disk {selectedVolume.DiskNumber}, but drive {windowsLetter}: is on Disk {windowsDisk} — a cloned drive's EFI partition is normally on the same disk.");

                // A clone copies the Disk ID, so another disk with the same ID is usually the
                // original it was cloned from — easy to mix up with the clone
                if (windowsDisk.HasValue && _diskDetails.TryGetValue(windowsDisk.Value, out DiskDetail windowsDiskDetail)
                    && DiskpartParser.IsMeaningfulDiskId(windowsDiskDetail.DiskId))
                {
                    var twins = _diskDetails.Values
                        .Where(d => d.Number != windowsDisk.Value && string.Equals(d.DiskId, windowsDiskDetail.DiskId, StringComparison.OrdinalIgnoreCase))
                        .Select(d => "Disk " + d.Number)
                        .ToList();
                    if (twins.Count > 0)
                        warnings.Add($"• {string.Join(", ", twins)} has the same Disk ID as Disk {windowsDisk} ({windowsDiskDetail.DiskId}) — one is probably the original the other was cloned from. Make sure {windowsLetter}: and the EFI volume are both on the CLONE.");
                }

                if (warnings.Count > 0)
                {
                    string warningText = "Please double-check your FAT32 volume selection:\n\n" + string.Join("\n\n", warnings) + "\n\nContinue anyway?";
                    if (MessageBox.Show(warningText, "Check Volume Selection", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;
                }
            }

            // Final confirmation summarizing exactly what will happen
            string tempLetter = UnusedDriveSelection_ddbox.SelectedItem.ToString().Substring(0, 1).ToUpper();
            string windowsRoot = ExtractDriveLetter(DriveSelection_ddbox.SelectedItem.ToString()) + @":\";

            if (!Regex.IsMatch(tempLetter, "^[A-Z]$"))
            {
                MessageBox.Show("The selected temporary drive letter is not valid.", "Invalid Letter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int efiDisk = selectedVolume.DiskNumber.Value;
            _physicalDisks.TryGetValue(efiDisk, out PhysicalDisk efiPhysicalDisk);
            string efiDiskName = efiPhysicalDisk != null && efiPhysicalDisk.Model.Length > 0
                ? $"{efiPhysicalDisk.Model} ({FormatBytes((long)efiPhysicalDisk.SizeBytes)})"
                : "unknown model";

            // Show each disk's Disk ID so the user can see the Windows drive and EFI volume pair up
            string DiskIdOf(int? disk) => disk.HasValue && _diskDetails.TryGetValue(disk.Value, out DiskDetail d) && d.DiskId.Length > 0 ? d.DiskId : "unknown";
            string pairing = windowsDisk == efiDisk
                ? "      ✓ same disk as the Windows installation\n"
                : "      ⚠ NOT the same disk as the Windows installation\n";

            string confirmMessage = "Fix Boot is ready to run:\n\n" +
                $"  • Windows installation:  {windowsRoot}Windows\n" +
                $"      on Disk {(windowsDisk.HasValue ? windowsDisk.ToString() : "?")} — Disk ID {DiskIdOf(windowsDisk)}\n" +
                $"  • EFI volume:  {volumeSelector}  {selectedVolume.FileSystem}  {selectedVolume.Size}\n" +
                $"      on Disk {efiDisk} — {efiDiskName} — Disk ID {DiskIdOf(efiDisk)}\n" +
                pairing +
                $"  • Temporary letter:  {tempLetter}:\n\n" +
                "The boot files on the selected EFI volume will be rebuilt (bcdboot).\n\nProceed?";

            if (MessageBox.Show(confirmMessage, "Confirm Fix Boot", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            bool letterAssigned = false;

            try
            {
                // Jump to the Simple tab so the user sees live progress
                MainTabs.SelectedIndex = 0;

                StatusBox.Text = "";
                StatusBox.AppendText("Assigning Drive Letter...");
                SetProgress("Assigning temporary drive letter...", 10);

                string letterToAssign = tempLetter;

                // The confirmation can sit open for any length of time — make sure the volume,
                // its disk and the Windows drive are still exactly what the user confirmed
                List<string> problems = VerifyFixBootTargets(selectedVolume, windowsLetter, windowsDisk, letterToAssign);
                if (problems.Count > 0)
                {
                    StatusBox.AppendText("\r\n\r\nStopped — the disks changed since 'Refresh'. Nothing was changed.");
                    SetProgress("Stopped: selection could not be verified.", 0);
                    MessageBox.Show("Fix Boot was stopped before changing anything:\n\n" + string.Join("\n", problems.Select(p => "  • " + p)) +
                        "\n\nClick 'Refresh' and check your selections again.",
                        "Selection Changed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Step 1: Assign a temporary drive letter to the FAT32 EFI volume. The volume is
                // described and checked in the SAME diskpart session that assigns the letter, so
                // it can't change between the check and the assign
                string output = ExecuteVerifiedDiskpartCommand(
                    new[] { $"select disk {efiDisk}", "detail disk", $"select {volumeSelector}", "detail volume" },
                    description =>
                    {
                        // Same disk as at Refresh (Disk ID, model, connection, volumes)...
                        if (!DiskpartParser.IsSameDiskAsConfirmed(description, efiDisk, efiDiskDetail))
                            return false;

                        // ...still holding this exact volume...
                        DiskDetail current = DiskpartParser.ParseDiskDetail(description, efiDisk);
                        if (!current.Volumes.Any(v => v.Number == selectedVolume.Number && v.Size == selectedVolume.Size
                                && v.FileSystem == selectedVolume.FileSystem && v.Letter == selectedVolume.Letter))
                            return false;

                        // ...and the selected volume really is on that disk only
                        return DiskpartParser.IsVolumeSelected(description, selectedVolume.Number)
                            && DiskpartParser.ParseDiskList(description).Select(d => d.Number).SequenceEqual(new[] { efiDisk });
                    },
                    new[] { $"assign letter {letterToAssign}" },
                    out bool assignSent);

                CMDOutputBox.AppendText(DeleteLines(output, 11));

                if (!assignSent)
                {
                    StatusBox.AppendText($"\r\n\r\nStopped — {volumeSelector} is no longer on Disk {efiDisk}. Nothing was changed.");
                    SetProgress("Stopped: selection could not be verified.", 0);
                    MessageBox.Show($"diskpart no longer shows {volumeSelector} on Disk {efiDisk}, so no letter was assigned and nothing was changed.\n\nClick 'Refresh' and try again.",
                        "Selection Changed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (output.Contains("successfully assigned the drive letter"))
                {
                    letterAssigned = true;
                    StatusBox.AppendText("\r\n\r\nDrive Letter Assigned Successfully");
                    SetProgress("Drive letter assigned. Rebuilding boot files...", 35);

                    // bcdboot writes to whatever the temporary letter points at, so confirm the
                    // letter really landed on the selected volume's disk before writing anything
                    if (!WaitForLetterOnDisk(letterToAssign, efiDisk, efiPhysicalDisk))
                    {
                        StatusBox.AppendText($"\r\n\r\nStopped — {letterToAssign}: is not on Disk {efiDisk}. No boot files were written.");
                        SetProgress("Stopped: temporary letter is not on the expected disk.", 0);
                        MessageBox.Show($"The temporary letter {letterToAssign}: did not end up on Disk {efiDisk}, so no boot files were written.\n\nClick 'Refresh' and try again.",
                            "Selection Changed", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
                else
                {
                    StatusBox.AppendText("\r\n\r\nFailed to assign drive letter");
                    SetProgress("Failed to assign drive letter.", 0);
                    MessageBox.Show("Failed to assign drive letter. The volume may already have a letter assigned or you may need administrator privileges.",
                        "Assignment Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Step 2: Rebuild boot files onto the EFI partition
                StatusBox.AppendText("\r\n\r\nFixing Boot Files...");

                string windowsDrive = windowsLetter + @":\";
                string bootDrive = letterToAssign.ToLower() + ":";
                string bcdbootCmd = $"bcdboot {windowsDrive}Windows /s {bootDrive} /f UEFI";
                string output1;

                using (Process p1 = new Process())
                {
                    p1.StartInfo.UseShellExecute = false;
                    p1.StartInfo.RedirectStandardOutput = true;
                    p1.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                    p1.StartInfo.CreateNoWindow = true;
                    p1.StartInfo.FileName = CMD_PATH;
                    p1.StartInfo.RedirectStandardInput = true;
                    p1.StartInfo.Verb = "runas";
                    p1.Start();

                    p1.StandardInput.WriteLine("cd C:\\Windows\\system32\\");
                    p1.StandardInput.WriteLine(bcdbootCmd);
                    p1.StandardInput.WriteLine("exit");
                    p1.StandardInput.Flush();
                    p1.StandardInput.Close();

                    output1 = p1.StandardOutput.ReadToEnd();
                    p1.WaitForExit();
                }

                CMDOutputBox.AppendText(DeleteLines(output1, 11));

                if (output1.Contains("Boot files successfully created."))
                {
                    StatusBox.AppendText("\r\n\r\nBoot Files Repaired Successfully");
                    SetProgress("Boot files repaired. Removing temporary drive letter...", 75);
                }
                else
                {
                    StatusBox.AppendText($"\r\n\r\nFailed to repair boot files\r\n\r\nCommand: {bcdbootCmd}");
                    SetProgress("Failed to repair boot files.", 0);
                    MessageBox.Show($"Failed to repair boot files.\n\nCommand that was run:\n{bcdbootCmd}\n\nCheck the Log tab for more information.",
                        "Boot Repair Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Step 3: Remove the temporary drive letter again
                StatusBox.AppendText("\r\n\r\nRemoving Temporary Drive Letter...");
                letterAssigned = false;

                if (RemoveTemporaryLetter(letterToAssign))
                {
                    StatusBox.AppendText("\r\n\r\nDrive Letter Removed Successfully");
                    StatusBox.AppendText("\r\n\r\n===== BOOT REPAIR COMPLETE =====");
                    SetProgress("Boot repair complete!", 100);
                    MessageBox.Show("Boot repair completed successfully!\n\nYour cloned drive should now boot properly.",
                        "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    StatusBox.AppendText("\r\n\r\nFailed to remove drive letter (not critical)");
                    SetProgress("Boot repaired, but temporary letter removal failed.", 90);
                    MessageBox.Show($"Boot files were repaired, but the temporary drive letter {letterToAssign}: could not be removed.\n\nYou may need to remove it manually using Disk Management.",
                        "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                StatusBox.AppendText($"\r\n\r\nERROR: {ex.Message}");
                SetProgress("Error during boot repair.", 0);
                MessageBox.Show($"An error occurred during boot repair:\n\n{ex.Message}\n\nMake sure you are running this application as Administrator.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Never leave the EFI partition mounted after a failure or error
                if (letterAssigned && !RemoveTemporaryLetter(tempLetter))
                {
                    StatusBox.AppendText($"\r\n\r\nThe temporary letter {tempLetter}: could not be removed — remove it in Disk Management.");
                    MessageBox.Show($"The temporary drive letter {tempLetter}: could not be removed from the EFI partition.\n\nRemove it manually using Disk Management.",
                        "Temporary Letter Still Assigned", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                RefreshDriveDropdowns();
            }
        }

        private void DriveSelection_ddbox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveSelection_ddbox.SelectedItem == null)
                return;

            try
            {
                string driveLetter = ExtractDriveLetter(DriveSelection_ddbox.SelectedItem.ToString());
                string driveSelected = driveLetter + @":\";
                DriveInfo driveInfo = new DriveInfo(driveSelected);

                string label = string.IsNullOrEmpty(driveInfo.VolumeLabel) ? "(no label)" : driveInfo.VolumeLabel;

                string serial = "unknown";
                try
                {
                    ManagementObject disk = new ManagementObject("win32_logicaldisk.deviceid=\"" + driveLetter + ":\"");
                    disk.Get();
                    if (disk["VolumeSerialNumber"] != null)
                        serial = disk["VolumeSerialNumber"].ToString();
                }
                catch { }

                int? diskNumber = GetDiskNumberForLetter(driveLetter);
                string diskText = diskNumber.HasValue ? "Disk " + diskNumber.Value : "unknown";

                long usedBytes = driveInfo.TotalSize - driveInfo.AvailableFreeSpace;
                double freePercent = driveInfo.TotalSize > 0 ? driveInfo.AvailableFreeSpace * 100.0 / driveInfo.TotalSize : 0;

                // Aligned label/value rows; Drive_Info uses a monospace font
                string Row(string name, string value) => name.PadRight(11) + value;

                StringBuilder info = new StringBuilder();
                info.AppendLine(Row("Letter:", driveSelected));
                info.AppendLine(Row("Label:", label));
                info.AppendLine(Row("Type:", driveInfo.DriveType + " — " + driveInfo.DriveFormat));
                info.AppendLine(Row("Disk:", diskText));
                info.AppendLine(Row("Serial:", serial));
                info.AppendLine();
                info.AppendLine(Row("Size:", FormatBytes(driveInfo.TotalSize)));
                info.AppendLine(Row("Used:", FormatBytes(usedBytes)));
                info.AppendLine(Row("Free:", $"{FormatBytes(driveInfo.AvailableFreeSpace)} ({freePercent:0}% free)"));
                info.AppendLine();
                info.AppendLine(Row("Protected:", IsDriveProtected(driveLetter) ? "Yes 🔒" : "No"));
                info.AppendLine(Row("Windows:", IsWindowsInstallation(driveSelected) ? "Yes ★" : "No"));

                Drive_Info.Text = info.ToString();

                Info_SelectedDriveText.Text = driveSelected;
                UpdateProtectButtonState(driveLetter);

                // Keep the FAT32 volume selection in sync with the chosen drive
                AutoSelectFat32Volume();
            }
            catch (Exception ex)
            {
                Drive_Info.Text = "Error reading drive information:\r\n" + ex.Message;
            }
        }

        /// <summary>
        /// Updates the lock/unlock protect button on the Info tab to reflect the selected drive's protection state.
        /// </summary>
        private void UpdateProtectButtonState(string driveLetter)
        {
            if (ToggleProtect_btn == null)
                return;

            if (IsDriveProtected(driveLetter))
            {
                ToggleProtect_btn.Content = "🔓 Unprotect Drive";
            }
            else
            {
                ToggleProtect_btn.Content = "🔒 Protect Drive";
            }
        }

        private void ToggleProtect_btn_Click(object sender, RoutedEventArgs e)
        {
            if (DriveSelection_ddbox.SelectedItem == null)
            {
                MessageBox.Show("Please select a drive first.", "No Drive Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string driveLetter = ExtractDriveLetter(DriveSelection_ddbox.SelectedItem.ToString());
            string driveSelected = driveLetter + @":\";

            // Protecting/unprotecting always applies to the whole disk (the drive itself and all its volumes)
            PhysicalDisk disk = GetPhysicalDiskForLetter(driveLetter);
            List<string> drivesOnSameDisk = disk != null && disk.Letters.Count > 0
                ? disk.Letters
                : GetDriveLettersOnSameDisk(driveLetter);
            string diskLine = disk != null ? $"Disk {disk.Number} — {DescribeDisk(disk)}\n\n" : "";

            if (IsDriveProtected(driveLetter))
            {
                string driveList = string.Join("\n", drivesOnSameDisk.Select(d => "  • " + d));
                var result = MessageBox.Show(
                    $"Are you sure you want to remove protection from this disk?\n\n{diskLine}The following drive(s) will become eligible for cleaning/formatting:\n\n{driveList}",
                    "Remove Protection?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    foreach (string drive in drivesOnSameDisk)
                    {
                        Properties.Settings.Default.ProtectedDrives.Remove(drive);
                    }
                    Properties.Settings.Default.ProtectedDrives.Remove(driveSelected.ToUpper());
                    if (disk != null)
                        RemoveProtectedDisk(disk.Key);
                    Properties.Settings.Default.Save();
                    RefreshProtectedDrivesList();
                    RefreshDriveDropdowns();
                    UpdateProtectButtonState(driveLetter);
                }
            }
            else
            {
                string driveList = string.Join("\n", drivesOnSameDisk.Select(d => "  • " + d));
                var result = MessageBox.Show(
                    $"This will protect the entire disk, including all its drive(s):\n\n{diskLine}{driveList}\n\nProtected drives cannot be cleaned or formatted through this tool.",
                    "Protect This Disk?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    foreach (string drive in drivesOnSameDisk)
                    {
                        AddProtectedDrive(drive);
                    }
                    AddProtectedDisk(disk);
                    RefreshProtectedDrivesList();
                    RefreshDriveDropdowns();
                    UpdateProtectButtonState(driveLetter);
                }
            }
        }

        private async void Refresh_btn_Click(object sender, RoutedEventArgs e)
        {
            Refresh_btn.IsEnabled = false;
            FixBoot_btn.IsEnabled = false;

            try
            {
                StatusBox.Text = "Getting volumes...";
                SetProgress("Refreshing drives and volumes...", indeterminate: true);

                // Refresh the drive dropdowns too, in case a drive was just connected
                RefreshDriveDropdowns();

                // Run the (slow, blocking) diskpart calls on a background thread so the UI stays responsive
                var refreshData = await Task.Run(() =>
                {
                    string volumes = ExecuteDiskpartCommand("list vol");

                    Dictionary<int, DiskDetail> details;
                    try { details = ReadAllDiskDetails().Item3; }
                    catch { details = new Dictionary<int, DiskDetail>(); }

                    var physicalDisks = details.Keys.ToDictionary(n => n, n => GetPhysicalDisk(n));

                    return Tuple.Create(volumes, details, physicalDisks);
                });

                string listoutput = refreshData.Item1;
                _diskDetails = refreshData.Item2;
                _physicalDisks = refreshData.Item3;
                _volumeToDisk = BuildVolumeToDiskMap(_diskDetails.Values);

                StatusBox.Text = "";
                StatusBox.AppendText(DeleteLines(listoutput, 11));
                CMDOutputBox.AppendText(DeleteLines(listoutput, 11));

                PopulateVolumesDropdown(listoutput);

                if (Volumes_dd.Items.Count > 0)
                {
                    StatusBox.AppendText("\r\r" + Volumes_dd.Items.Count + " volume(s) found.");
                    AutoSelectFat32Volume();
                    SetProgress("Ready. " + Volumes_dd.Items.Count + " volume(s) found.", 100);
                }
                else
                {
                    StatusBox.AppendText("\r\rNo volumes found.");
                    SetProgress("No volumes found.", 100);
                }
            }
            catch (Exception ex)
            {
                StatusBox.Text = "Error getting volumes: " + ex.Message;
                SetProgress("Error refreshing volumes.", 0);
                MessageBox.Show("Failed to get volumes. Make sure you run this application as Administrator.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Refresh_btn.IsEnabled = true;
                FixBoot_btn.IsEnabled = true;
            }
        }

        private void CreatorLink_MouseDown(object sender, MouseButtonEventArgs e)
        {
            System.Diagnostics.Process.Start("https://CoolshrimpModz.com");
        }

        private async void Get_Disks_btn_Click(object sender, RoutedEventArgs e)
        {
            Get_Disks_btn.IsEnabled = false;
            try
            {
                ExtrasInfoBox.Text = "Getting disks...";
                Disk_list_dd.Items.Clear();

                // diskpart and WMI are slow — keep the UI responsive while they run
                var listing = await Task.Run(() =>
                {
                    var diskpartData = ReadAllDiskDetails();
                    var wmiDisks = diskpartData.Item2.ToDictionary(d => d.Number, d => GetPhysicalDisk(d.Number));
                    return Tuple.Create(diskpartData, wmiDisks);
                });

                string listdisksoutput = listing.Item1.Item1;
                Dictionary<int, DiskDetail> details = listing.Item1.Item3;
                Dictionary<int, PhysicalDisk> physicalDisks = listing.Item2;

                ExtrasInfoBox.Text = "";
                ExtrasInfoBox.AppendText(DeleteLines(listdisksoutput, 11));

                foreach (DiskListEntry entry in listing.Item1.Item2)
                {
                    PhysicalDisk physical = physicalDisks[entry.Number];
                    DiskDetail detail = details[entry.Number];

                    // Letters from both Windows and diskpart, paired with the disk number,
                    // hardware identity and Disk ID — all re-checked before Clean/Convert
                    List<string> letters = (physical != null ? physical.Letters : new List<string>())
                        .Union(detail.Letters)
                        .Distinct()
                        .OrderBy(l => l)
                        .ToList();

                    // Show enough to recognise the disk (size, model, letters) — a bare
                    // "Disk 2" is far too easy to mix up with another disk
                    var parts = new List<string> { $"Disk {entry.Number}", entry.Size };
                    string model = physical != null && physical.Model.Length > 0 ? physical.Model : detail.Model;
                    if (!string.IsNullOrEmpty(model))
                        parts.Add(model);
                    if (letters.Count > 0)
                        parts.Add(FormatLetters(letters));
                    if (!string.Equals(entry.Status, "Online", StringComparison.OrdinalIgnoreCase))
                        parts.Add("(" + entry.Status + ")");
                    if (!detail.IsComplete || physical == null)
                        parts.Add("(unreadable)");
                    if (detail.IsInUseByWindows || FindProtectionMatch(physical, detail.DiskId) != null
                        || letters.Any(l => IsLetterInProtectedList(l) || IsHostSystemLetter(l)))
                        parts.Add("🔒");

                    string text = string.Join("  ", parts);
                    Disk_list_dd.Items.Add(new ComboBoxItem
                    {
                        Content = text,
                        ToolTip = text,
                        Tag = new DiskChoice
                        {
                            Number = entry.Number,
                            Key = physical != null ? physical.Key : "",
                            DiskId = detail.DiskId,
                            Letters = letters
                        }
                    });
                }

                if (Disk_list_dd.Items.Count > 0)
                {
                    ExtrasInfoBox.AppendText("\r\r" + Disk_list_dd.Items.Count + " disk(s) found.");
                }
                else
                {
                    ExtrasInfoBox.AppendText("\r\rNo disks found.");
                }
            }
            catch (Exception ex)
            {
                ExtrasInfoBox.Text = "Error getting disks: " + ex.Message;
                MessageBox.Show("Failed to get disks. Make sure you run this application as Administrator.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Get_Disks_btn.IsEnabled = true;
            }
        }

        private void ProtectDisk_btn_Click(object sender, RoutedEventArgs e)
        {
            DiskChoice choice = GetSelectedDiskChoice();
            if (choice == null)
                return;

            try
            {
                DiskSafetyCheck check = InspectDisk(choice.Number);

                if (check.Disk == null || string.IsNullOrEmpty(check.Disk.Key) || !check.Detail.IsComplete)
                {
                    MessageBox.Show($"Disk {choice.Number} could not be read, so it was not protected.\n\nClick 'List Disks' and try again.",
                        "Disk Not Read", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Same pairing as Clean/Convert: it must still be the disk the user picked
                if (check.Disk.Key != choice.Key || !SameLetters(check.Letters, choice.Letters))
                {
                    MessageBox.Show($"Disk {choice.Number} is no longer the disk that was listed — disks were added or removed.\n\nClick 'List Disks' and select the disk again.",
                        "Disk Changed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // The disk itself is protected by its hardware identity, so disks without
                // drive letters (empty, offline, hidden) can be protected too
                List<string> drivesOnDisk = check.Letters;

                StringBuilder message = new StringBuilder();
                message.AppendLine("The following disk will be protected:");
                message.AppendLine();
                message.AppendLine(FormatDiskSummary(check));
                message.AppendLine();
                message.AppendLine("Protected drives cannot be cleaned or converted through this tool.");

                if (MessageBox.Show(message.ToString(), "Protect Disk?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    foreach (string drive in drivesOnDisk)
                    {
                        AddProtectedDrive(drive);
                    }
                    AddProtectedDisk(check.Disk, check.Detail.DiskId);
                    RefreshProtectedDrivesList();
                    RefreshDriveDropdowns();
                    MessageBox.Show($"Disk {choice.Number} — {DescribeDisk(check.Disk)} is now protected" +
                        (drivesOnDisk.Count > 0 ? $", including {FormatLetters(drivesOnDisk)}." : "."),
                        "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to protect disk: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ConvertToGPT_btn_Click(object sender, RoutedEventArgs e)
        {
            DiskChoice choice = GetSelectedDiskChoice();
            if (choice == null)
                return;

            try
            {
                // Refuses protected disks, the Windows disk, unreadable disks and renumbered disks
                DiskSafetyCheck check = CheckDiskBeforeDestructiveOperation(choice, "Convert");
                if (check == null)
                    return;

                string diskInfo = FormatDiskSummary(check) + "\nNote: Converting to GPT requires the disk to be empty.";

                if (!ConfirmDestructiveOperation(diskInfo, "convert to GPT"))
                    return;

                // The confirmation can sit open for any length of time — check again right before running
                DiskSafetyCheck final = RecheckDiskJustBeforeRunning(choice, check, "Convert");
                if (final == null)
                    return;

                // ...and once more inside the very diskpart session that converts it
                string output4 = ExecuteVerifiedDiskpartCommand(
                    new[] { $"select disk {choice.Number}", "detail disk" },
                    description => DiskpartParser.IsSameDiskAsConfirmed(description, choice.Number, final.Detail),
                    new[] { "convert GPT" },
                    out bool convertSent);

                if (!convertSent)
                {
                    ExtrasInfoBox.Text = DeleteLines(output4, 11);
                    MessageBox.Show($"Disk {choice.Number} changed at the last moment, so it was NOT converted.\n\nClick 'List Disks' and check your selection again.",
                        "Disk Changed - Not Converted", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                ExtrasInfoBox.Text = "";
                ExtrasInfoBox.AppendText(DeleteLines(output4, 11));

                if (output4.Contains("successfully converted"))
                {
                    MessageBox.Show("Disk successfully converted to GPT.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    UpdateDiskChoiceAfterChange(choice, final.Disk.Key);
                }
                else if (output4.Contains("The specified disk is not convertible") || output4.Contains("already a GPT disk"))
                {
                    MessageBox.Show("The disk cannot be converted. It may already be GPT or contain data.\n\nYou must clean the disk first before converting.",
                        "Conversion Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show("Failed to convert disk to GPT. Check the detailed output.", "Conversion Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to convert disk: {ex.Message}\n\nMake sure you are running this application as Administrator.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Clean_Disk_btn_Click(object sender, RoutedEventArgs e)
        {
            DiskChoice choice = GetSelectedDiskChoice();
            if (choice == null)
                return;

            try
            {
                // Refuses protected disks, the Windows disk, unreadable disks and renumbered disks
                DiskSafetyCheck check = CheckDiskBeforeDestructiveOperation(choice, "Clean");
                if (check == null)
                    return;

                // Show the actual disk being wiped — model, size, serial and its volumes
                if (!ConfirmDestructiveOperation(FormatDiskSummary(check), "CLEAN (wipe all data from)"))
                    return;

                // Second confirmation names the disk again so a wrong pick is hard to miss
                string model = check.Disk.Model.Length > 0 ? check.Disk.Model : "this disk";
                MessageBoxResult finalConfirm = MessageBox.Show(
                    $"FINAL WARNING!\n\nThis will PERMANENTLY DELETE ALL DATA on:\n\n  Disk {choice.Number} — {model} ({FormatBytes((long)check.Disk.SizeBytes)})\n\nClick YES only if you are absolutely certain.",
                    "Final Confirmation Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Stop);

                if (finalConfirm != MessageBoxResult.Yes)
                    return;

                // The confirmations can sit open for any length of time — check again right before running
                DiskSafetyCheck final = RecheckDiskJustBeforeRunning(choice, check, "Clean");
                if (final == null)
                    return;

                // ...and once more inside the very diskpart session that cleans it
                string output5 = ExecuteVerifiedDiskpartCommand(
                    new[] { $"select disk {choice.Number}", "detail disk" },
                    description => DiskpartParser.IsSameDiskAsConfirmed(description, choice.Number, final.Detail),
                    new[] { "clean" },
                    out bool cleanSent);

                ExtrasInfoBox.Text = "";
                ExtrasInfoBox.AppendText(DeleteLines(output5, 11));

                if (!cleanSent)
                {
                    MessageBox.Show($"Disk {choice.Number} changed at the last moment, so it was NOT cleaned.\n\nClick 'List Disks' and check your selection again.",
                        "Disk Changed - Not Cleaned", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (output5.Contains("succeeded in cleaning") || output5.Contains("successfully cleaned"))
                {
                    MessageBox.Show("Disk successfully cleaned.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    UpdateDiskChoiceAfterChange(choice, final.Disk.Key);
                }
                else
                {
                    MessageBox.Show("Failed to clean disk. Check the detailed output.", "Clean Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                // Letters on the cleaned disk are gone; show the drives as they are now
                RefreshDriveDropdowns();
                RefreshProtectedDrivesList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clean disk: {ex.Message}\n\nMake sure you are running this application as Administrator.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Show_disk_manager_btn_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(DISKMGMT_PATH);
        }
    
        private void StatusBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // TODO: handle the TextChanged event
        }
    }
}
