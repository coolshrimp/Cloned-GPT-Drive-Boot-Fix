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
        /// Represents a single volume as parsed from 'diskpart list volume' output.
        /// </summary>
        private class VolumeEntry
        {
            public int Number;
            public string Letter;   // may be empty if no letter assigned
            public string Label;
            public string FileSystem;
            public string Size;

            public string DiskpartSelector => "Volume " + Number;
        }

        private readonly List<VolumeEntry> _parsedVolumes = new List<VolumeEntry>();

        public MainWindow()
        {
            InitializeComponent();

            // Initialize protected drives collection if null
            if (Properties.Settings.Default.ProtectedDrives == null)
            {
                Properties.Settings.Default.ProtectedDrives = new System.Collections.Specialized.StringCollection();
                Properties.Settings.Default.Save();
            }

            // Check for first run
            if (Properties.Settings.Default.IsFirstRun)
            {
                ShowFirstRunDialog();
                Properties.Settings.Default.IsFirstRun = false;
                Properties.Settings.Default.Save();
            }

            RefreshDriveDropdowns();
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
        /// Populates the cloned-drive dropdown and the list of currently unused drive letters.
        /// Safe to call again later (e.g. after plugging in a drive) since it clears first.
        /// </summary>
        private void RefreshDriveDropdowns()
        {
            DriveSelection_ddbox.Items.Clear();

            foreach (var drive in Environment.GetLogicalDrives())
            {
                DriveInfo driveInfo = new DriveInfo(drive);
                if (driveInfo.IsReady)
                {
                    bool hasWindows = Directory.Exists(System.IO.Path.Combine(drive, "Windows"));
                    string windowsIcon = hasWindows ? "🪟 " : "";
                    string display = windowsIcon + driveInfo.Name + "     " + driveInfo.VolumeLabel;
                    DriveSelection_ddbox.Items.Add(display);
                }
            }

            UnusedDriveSelection_ddbox.Items.Clear();

            ArrayList driveLetters = new ArrayList(26); // Allocate space for alphabet
            for (int i = 65; i < 91; i++) // increment from ASCII values for A-Z
            {
                driveLetters.Add(Convert.ToChar(i)); // Add uppercase letters to possible drive letters
            }

            foreach (string drive in Directory.GetLogicalDrives())
            {
                driveLetters.Remove(drive[0]); // removed used drive letters from possible drive letters
            }

            foreach (char drive in driveLetters)
            {
                UnusedDriveSelection_ddbox.Items.Add(drive + ":\\"); // add unused drive letters to the combo box
            }
        }

        /// <summary>
        /// Refreshes the protected drives list box from settings
        /// </summary>
        private void RefreshProtectedDrivesList()
        {
            ProtectedDrives_ListBox.Items.Clear();

            if (Properties.Settings.Default.ProtectedDrives != null)
            {
                foreach (string drive in Properties.Settings.Default.ProtectedDrives)
                {
                    ProtectedDrives_ListBox.Items.Add(drive);
                }
            }
        }

        private void RemoveProtection_btn_Click(object sender, RoutedEventArgs e)
        {
            if (ProtectedDrives_ListBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a drive from the protected list to remove.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedDrive = ProtectedDrives_ListBox.SelectedItem.ToString();

            var result = MessageBox.Show(
                $"Are you sure you want to remove protection from {selectedDrive}?\n\nThis drive will then be eligible for cleaning/formatting.",
                "Remove Protection?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                Properties.Settings.Default.ProtectedDrives.Remove(selectedDrive);
                Properties.Settings.Default.Save();
                RefreshProtectedDrivesList();
            }
        }

        private void RefreshProtected_btn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Rescan all connected, ready drives and protect every one of them so that
                // nothing can be cleaned/formatted without the user explicitly unprotecting it first.
                List<string> newlyProtected = new List<string>();

                if (Properties.Settings.Default.ProtectedDrives == null)
                {
                    Properties.Settings.Default.ProtectedDrives = new System.Collections.Specialized.StringCollection();
                }

                foreach (var drive in Environment.GetLogicalDrives())
                {
                    try
                    {
                        DriveInfo driveInfo = new DriveInfo(drive);
                        if (!driveInfo.IsReady)
                            continue;

                        // Protect every drive letter on the same physical disk, not just this one.
                        foreach (string letterOnDisk in GetDriveLettersOnSameDisk(drive))
                        {
                            if (!Properties.Settings.Default.ProtectedDrives.Contains(letterOnDisk))
                            {
                                Properties.Settings.Default.ProtectedDrives.Add(letterOnDisk);
                                newlyProtected.Add(letterOnDisk);
                            }
                        }
                    }
                    catch { }
                }

                if (newlyProtected.Count > 0)
                {
                    Properties.Settings.Default.Save();
                }

                RefreshProtectedDrivesList();

                if (DriveSelection_ddbox.SelectedItem != null)
                {
                    string driveLetter = ExtractDriveLetter(DriveSelection_ddbox.SelectedItem.ToString());
                    UpdateProtectButtonState(driveLetter);
                }

                if (newlyProtected.Count > 0)
                {
                    MessageBox.Show($"Rescan complete. {newlyProtected.Count} drive(s) were newly protected:\n\n" + string.Join("\n", newlyProtected),
                        "Rescan Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Rescan complete. All connected drives are already protected.",
                        "Rescan Complete", MessageBoxButton.OK, MessageBoxImage.Information);
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
                string query = $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{normalizedLetter}:'}} WHERE AssocClass=Win32_LogicalDiskToPartition";
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (ManagementObject partition in searcher.Get())
                    {
                        string partitionQuery = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition";
                        using (var logicalDiskSearcher = new ManagementObjectSearcher(partitionQuery))
                        {
                            foreach (ManagementObject logicalDisk in logicalDiskSearcher.Get())
                            {
                                string letter = logicalDisk["DeviceID"].ToString() + "\\";
                                if (!result.Contains(letter))
                                {
                                    result.Add(letter);
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
        /// Parses 'diskpart list volume' output and populates the Volumes_dd dropdown
        /// with friendly, readable entries (Volume number, letter, filesystem, size).
        /// </summary>
        private void PopulateVolumesDropdown(string diskpartListVolumeOutput)
        {
            _parsedVolumes.Clear();
            Volumes_dd.Items.Clear();

            // Typical diskpart line:
            //   Volume 2     D   Label        FAT32  Partition    100 MB  Healthy    System
            var lineRegex = new Regex(
                @"Volume\s+(?<num>\d+)\s+(?<ltr>[A-Za-z])?\s*(?<label>.*?)\s+(?<fs>FAT32|NTFS|exFAT|ReFS)?\s*(?<type>Partition|Removable|DVD-ROM)?\s+(?<size>[\d.,]+\s?(?:B|KB|MB|GB|TB))",
                RegexOptions.IgnoreCase);

            foreach (string line in diskpartListVolumeOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                Match m = lineRegex.Match(line);
                if (!m.Success)
                    continue;

                var entry = new VolumeEntry
                {
                    Number = int.Parse(m.Groups["num"].Value),
                    Letter = m.Groups["ltr"].Success ? m.Groups["ltr"].Value.ToUpper() : "",
                    Label = m.Groups["label"].Value.Trim(),
                    FileSystem = m.Groups["fs"].Success ? m.Groups["fs"].Value.ToUpper() : "",
                    Size = m.Groups["size"].Success ? m.Groups["size"].Value.Trim() : ""
                };

                _parsedVolumes.Add(entry);

                string letterDisplay = string.IsNullOrEmpty(entry.Letter) ? "--" : entry.Letter + ":";
                string fsDisplay = string.IsNullOrEmpty(entry.FileSystem) ? "Unknown" : entry.FileSystem;
                string labelDisplay = string.IsNullOrEmpty(entry.Label) ? "(no label)" : entry.Label;

                string friendlyText = $"Volume {entry.Number}   [{letterDisplay}]   {fsDisplay}   {labelDisplay}   {entry.Size}";

                Volumes_dd.Items.Add(new ComboBoxItem
                {
                    Content = friendlyText,
                    Tag = entry.DiskpartSelector
                });
            }
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
        /// Automatically selects the FAT32 volume that belongs to the currently selected cloned drive.
        /// Prefers the FAT32 volume immediately following the drive's own volume (typical EFI partition
        /// layout on a cloned disk). The user can still change the selection afterward.
        /// </summary>
        private void AutoSelectFat32Volume()
        {
            if (DriveSelection_ddbox.SelectedItem == null || _parsedVolumes.Count == 0)
                return;

            string selectedDriveLetter = ExtractDriveLetter(DriveSelection_ddbox.SelectedItem.ToString()).ToUpper();

            int matchIndex = _parsedVolumes.FindIndex(v => v.Letter == selectedDriveLetter);

            VolumeEntry fat32Match = null;

            if (matchIndex >= 0)
            {
                // Look forward from the drive's own volume for the next FAT32 volume
                for (int i = matchIndex + 1; i < _parsedVolumes.Count; i++)
                {
                    if (_parsedVolumes[i].FileSystem == "FAT32")
                    {
                        fat32Match = _parsedVolumes[i];
                        break;
                    }
                }
            }

            // Fallback: any FAT32 volume at all (excluding the drive's own volume)
            if (fat32Match == null)
            {
                fat32Match = _parsedVolumes.FirstOrDefault(v => v.FileSystem == "FAT32" && v.Letter != selectedDriveLetter);
            }

            if (fat32Match == null)
                return;

            foreach (var obj in Volumes_dd.Items)
            {
                if (obj is ComboBoxItem cbi && cbi.Tag?.ToString() == fat32Match.DiskpartSelector)
                {
                    Volumes_dd.SelectedItem = cbi;
                    break;
                }
            }
        }

        /// <summary>
        /// Checks if a drive letter is in the protected list
        /// </summary>
        private bool IsDriveProtected(string driveLetter)
        {
            if (Properties.Settings.Default.ProtectedDrives == null)
                return false;

            string normalizedLetter = driveLetter.Substring(0, 1).ToUpper();

            foreach (string protectedDrive in Properties.Settings.Default.ProtectedDrives)
            {
                if (protectedDrive.StartsWith(normalizedLetter))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Shows first run dialog to protect system drives
        /// </summary>
        private void ShowFirstRunDialog()
        {
            StringBuilder message = new StringBuilder();
            message.AppendLine("Welcome to Cloned GPT Drive - Boot Fix!");
            message.AppendLine();
            message.AppendLine("For your safety, this tool will protect your system drives.");
            message.AppendLine("The following drives have been detected:");
            message.AppendLine();

            List<string> systemDrives = new List<string>();

            foreach (var drive in Environment.GetLogicalDrives())
            {
                try
                {
                    DriveInfo driveInfo = new DriveInfo(drive);
                    if (driveInfo.IsReady)
                    {
                        // Check if it's the system drive or a drive with Windows installed
                        bool isSystemDrive = drive.StartsWith(System.IO.Path.GetPathRoot(Environment.SystemDirectory));
                        bool hasWindows = Directory.Exists(System.IO.Path.Combine(drive, "Windows"));

                        if (isSystemDrive || hasWindows)
                        {
                            systemDrives.Add(drive);
                            message.AppendLine($"  {drive} - {driveInfo.VolumeLabel} ({FormatBytes(driveInfo.TotalSize)}) [SYSTEM]");
                        }
                        else
                        {
                            message.AppendLine($"  {drive} - {driveInfo.VolumeLabel} ({FormatBytes(driveInfo.TotalSize)})");
                        }
                    }
                }
                catch { }
            }

            message.AppendLine();
            message.AppendLine("System drives will be automatically protected from formatting.");
            message.AppendLine("You can manage protected drives in the List Disks tab.");

            MessageBox.Show(message.ToString(), "First Run - Drive Protection", MessageBoxButton.OK, MessageBoxImage.Information);

            // Add system drives to protected list
            foreach (string systemDrive in systemDrives)
            {
                if (!Properties.Settings.Default.ProtectedDrives.Contains(systemDrive))
                {
                    Properties.Settings.Default.ProtectedDrives.Add(systemDrive);
                }
            }

            Properties.Settings.Default.Save();
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

            if (UnusedDriveSelection_ddbox.SelectedItem == null)
            {
                MessageBox.Show("Please select an unused drive letter.", "No Letter Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                StatusBox.Text = "";
                StatusBox.AppendText("Assigning Drive Letter...");
                SetProgress("Assigning temporary drive letter...", 10);

                string letterToAssign = UnusedDriveSelection_ddbox.SelectedItem.ToString().Substring(0, 1);

                // Step 1: Assign a temporary drive letter to the FAT32 EFI volume
                string output = ExecuteDiskpartCommand(
                    $"select {volumeSelector}",
                    $"assign letter {letterToAssign}"
                );

                CMDOutputBox.AppendText(DeleteLines(output, 11));

                if (output.Contains("successfully assigned the drive letter"))
                {
                    StatusBox.AppendText("\r\n\r\nDrive Letter Assigned Successfully");
                    SetProgress("Drive letter assigned. Rebuilding boot files...", 35);
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

                Process p1 = new Process();
                p1.StartInfo.UseShellExecute = false;
                p1.StartInfo.RedirectStandardOutput = true;
                p1.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                p1.StartInfo.CreateNoWindow = true;
                p1.StartInfo.FileName = CMD_PATH;
                p1.StartInfo.RedirectStandardInput = true;
                p1.StartInfo.Verb = "runas";
                p1.Start();

                string windowsDrive = ExtractDriveLetter(DriveSelection_ddbox.SelectedItem.ToString()) + @":\";
                string bootDrive = UnusedDriveSelection_ddbox.SelectedItem.ToString().Substring(0, 2).ToLower();
                string bcdbootCmd = $"bcdboot {windowsDrive}Windows /s {bootDrive} /f UEFI";

                p1.StandardInput.WriteLine("cd C:\\Windows\\system32\\");
                p1.StandardInput.WriteLine(bcdbootCmd);
                p1.StandardInput.WriteLine("exit");
                p1.StandardInput.Flush();
                p1.StandardInput.Close();

                string output1 = p1.StandardOutput.ReadToEnd();
                p1.WaitForExit();

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

                string output2 = ExecuteDiskpartCommand(
                    $"select {volumeSelector}",
                    $"remove letter {letterToAssign}"
                );

                CMDOutputBox.AppendText(DeleteLines(output2, 11));

                if (output2.Contains("successfully removed the drive letter"))
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
                    MessageBox.Show("Boot files were repaired, but the temporary drive letter could not be removed.\n\nYou may need to remove it manually using Disk Management.",
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

                ManagementObject disk = new ManagementObject("win32_logicaldisk.deviceid=\"" + driveLetter + ":\"");
                disk.Get();

                StringBuilder info = new StringBuilder();
                info.AppendLine("Drive Letter:");
                info.AppendLine(driveSelected);
                info.AppendLine();
                info.AppendLine("Drive Label:");
                info.AppendLine(driveInfo.VolumeLabel);
                info.AppendLine();
                info.AppendLine("Drive ID:");
                info.AppendLine(disk["VolumeSerialNumber"].ToString());
                info.AppendLine();
                info.AppendLine("Drive Size:");
                info.AppendLine(FormatBytes(driveInfo.TotalSize));
                info.AppendLine();
                info.AppendLine("Free Space:");
                info.AppendLine(FormatBytes(driveInfo.AvailableFreeSpace));

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
            List<string> drivesOnSameDisk = GetDriveLettersOnSameDisk(driveLetter);

            if (IsDriveProtected(driveLetter))
            {
                string driveList = string.Join("\n", drivesOnSameDisk.Select(d => "  • " + d));
                var result = MessageBox.Show(
                    $"Are you sure you want to remove protection from this disk?\n\nThe following drive(s) will become eligible for cleaning/formatting:\n\n{driveList}",
                    "Remove Protection?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    foreach (string drive in drivesOnSameDisk)
                    {
                        Properties.Settings.Default.ProtectedDrives.Remove(drive);
                    }
                    Properties.Settings.Default.Save();
                    RefreshProtectedDrivesList();
                    UpdateProtectButtonState(driveLetter);
                }
            }
            else
            {
                string driveList = string.Join("\n", drivesOnSameDisk.Select(d => "  • " + d));
                var result = MessageBox.Show(
                    $"This will protect the entire disk, including all its drive(s):\n\n{driveList}\n\nProtected drives cannot be cleaned or formatted through this tool.",
                    "Protect This Disk?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    foreach (string drive in drivesOnSameDisk)
                    {
                        AddProtectedDrive(drive);
                    }
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

                // Run the (slow, blocking) diskpart call on a background thread so the UI stays responsive
                string listoutput = await Task.Run(() => ExecuteDiskpartCommand("list vol"));

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

        private void Button_Click_2(object sender, RoutedEventArgs e)
        {
           
        }

        private void unmount_disk_btn_Click(object sender, RoutedEventArgs e)
        {

        }

        private void CreatorLink_MouseDown(object sender, MouseButtonEventArgs e)
        {
            System.Diagnostics.Process.Start("https://CoolshrimpModz.com");
        }

        private void Get_Disks_btn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ExtrasInfoBox.Text = "Getting disks...";

                string listdisksoutput = ExecuteDiskpartCommand("list disk");

                ExtrasInfoBox.Text = "";
                ExtrasInfoBox.AppendText(DeleteLines(listdisksoutput, 11));

                Disk_list_dd.Items.Clear();

                for (int i = 0; i < 20; i++)
                {
                    if (ExtrasInfoBox.Text.Contains("Disk " + i))
                    {
                        Disk_list_dd.Items.Add("Disk " + i);
                    }
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
        }

        private void ProtectDisk_btn_Click(object sender, RoutedEventArgs e)
        {
            if (Disk_list_dd.SelectedItem == null)
            {
                MessageBox.Show("Please select a disk first.", "No Disk Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedDisk = Disk_list_dd.SelectedItem.ToString();

            try
            {
                string diskOutput = ExecuteDiskpartCommand($"select {selectedDisk}", "list volume");

                // Find drive letters belonging to this disk
                List<string> drivesOnDisk = new List<string>();
                foreach (var driveLetterObj in Environment.GetLogicalDrives())
                {
                    string letter = driveLetterObj.Substring(0, 1).ToUpper();
                    if (diskOutput.Contains($" {letter} "))
                    {
                        drivesOnDisk.Add(driveLetterObj);
                    }
                }

                if (drivesOnDisk.Count == 0)
                {
                    MessageBox.Show($"No assigned drive letters were found on {selectedDisk}.\n\nOnly disks with visible drive letters can be protected.", 
                        "No Drives Found", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                StringBuilder message = new StringBuilder();
                message.AppendLine($"The following drives on {selectedDisk} will be protected:");
                message.AppendLine();
                foreach (string drive in drivesOnDisk)
                {
                    message.AppendLine($"  • {drive}");
                }
                message.AppendLine();
                message.AppendLine("Protected drives cannot be cleaned or converted through this tool.");

                if (MessageBox.Show(message.ToString(), "Protect Disk?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    foreach (string drive in drivesOnDisk)
                    {
                        AddProtectedDrive(drive);
                    }
                    MessageBox.Show($"{drivesOnDisk.Count} drive(s) are now protected.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to protect disk: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ConvertToGPT_btn_Click(object sender, RoutedEventArgs e)
        {
            // Validate selection
            if (Disk_list_dd.SelectedItem == null)
            {
                MessageBox.Show("Please select a disk first.", "No Disk Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedDisk = Disk_list_dd.SelectedItem.ToString();

            // Check if any drive on this disk is protected
            string diskOutput = ExecuteDiskpartCommand($"select {selectedDisk}", "list volume");

            bool hasProtectedDrive = false;
            foreach (string protectedDrive in Properties.Settings.Default.ProtectedDrives)
            {
                if (diskOutput.Contains(protectedDrive.Substring(0, 1)))
                {
                    hasProtectedDrive = true;
                    break;
                }
            }

            if (hasProtectedDrive)
            {
                MessageBox.Show($"Cannot convert {selectedDisk} to GPT.\n\nThis disk contains protected system drives.\n\nYou can manage protected drives in the List Disks tab.", 
                    "Protected Drive", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Get disk information for confirmation
            StringBuilder diskInfo = new StringBuilder();
            diskInfo.AppendLine($"Disk: {selectedDisk}");
            diskInfo.AppendLine("\nNote: Converting to GPT requires the disk to be empty.");

            if (!ConfirmDestructiveOperation(diskInfo.ToString(), "convert to GPT"))
            {
                return;
            }

            Process p4 = new Process();
            p4.StartInfo.UseShellExecute = false;
            p4.StartInfo.RedirectStandardOutput = true;
            p4.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p4.StartInfo.CreateNoWindow = true;
            p4.StartInfo.FileName = DISKPART_PATH;
            p4.StartInfo.RedirectStandardInput = true;
            p4.StartInfo.Verb = "runas";
            p4.Start();
            p4.StandardInput.WriteLine("select " + selectedDisk);
            p4.StandardInput.WriteLine("convert GPT");
            p4.StandardInput.WriteLine("exit");
            p4.StandardInput.Flush();
            p4.StandardInput.Close();

            string output4 = p4.StandardOutput.ReadToEnd();
            p4.WaitForExit();

            ExtrasInfoBox.Text = "";
            ExtrasInfoBox.AppendText(DeleteLines(output4, 11));

            if (output4.Contains("successfully converted"))
            {
                MessageBox.Show("Disk successfully converted to GPT.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void Clean_Disk_btn_Click(object sender, RoutedEventArgs e)
        {
            // Validate selection
            if (Disk_list_dd.SelectedItem == null)
            {
                MessageBox.Show("Please select a disk first.", "No Disk Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedDisk = Disk_list_dd.SelectedItem.ToString();

            // Check if any drive on this disk is protected
            string diskOutput = ExecuteDiskpartCommand($"select {selectedDisk}", "list volume");

            List<string> protectedDrivesOnDisk = new List<string>();
            foreach (string protectedDrive in Properties.Settings.Default.ProtectedDrives)
            {
                if (diskOutput.Contains(protectedDrive.Substring(0, 1)))
                {
                    protectedDrivesOnDisk.Add(protectedDrive);
                }
            }

            if (protectedDrivesOnDisk.Count > 0)
            {
                StringBuilder message = new StringBuilder();
                message.AppendLine($"CANNOT CLEAN {selectedDisk}!");
                message.AppendLine();
                message.AppendLine("This disk contains the following PROTECTED drives:");
                foreach (string drive in protectedDrivesOnDisk)
                {
                    message.AppendLine($"  • {drive}");
                }
                message.AppendLine();
                message.AppendLine("Protected drives cannot be cleaned to prevent data loss.");
                message.AppendLine("You can manage protected drives in the List Disks tab.");

                MessageBox.Show(message.ToString(), "Protected Drive - Cannot Clean", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Get disk information for better confirmation
            StringBuilder diskInfo = new StringBuilder();
            diskInfo.AppendLine($"Disk: {selectedDisk}");
            diskInfo.AppendLine($"\n{ExtrasInfoBox.Text.Substring(0, Math.Min(200, ExtrasInfoBox.Text.Length))}...");

            if (!ConfirmDestructiveOperation(diskInfo.ToString(), "CLEAN (wipe all data from)"))
            {
                return;
            }

            // Second confirmation with typing requirement
            MessageBoxResult finalConfirm = MessageBox.Show(
                "FINAL WARNING!\n\nThis will PERMANENTLY DELETE ALL DATA!\n\nClick YES only if you are absolutely certain.",
                "Final Confirmation Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Stop);

            if (finalConfirm != MessageBoxResult.Yes)
            {
                return;
            }

            Process p5 = new Process();
            p5.StartInfo.UseShellExecute = false;
            p5.StartInfo.RedirectStandardOutput = true;
            p5.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p5.StartInfo.CreateNoWindow = true;
            p5.StartInfo.FileName = DISKPART_PATH;
            p5.StartInfo.RedirectStandardInput = true;
            p5.StartInfo.Verb = "runas";
            p5.Start();
            p5.StandardInput.WriteLine("select " + selectedDisk);
            p5.StandardInput.WriteLine("clean");
            p5.StandardInput.WriteLine("exit");
            p5.StandardInput.Flush();
            p5.StandardInput.Close();

            string output5 = p5.StandardOutput.ReadToEnd();
            p5.WaitForExit();

            ExtrasInfoBox.Text = "";
            ExtrasInfoBox.AppendText(DeleteLines(output5, 11));

            if (output5.Contains("successfully cleaned"))
            {
                MessageBox.Show("Disk successfully cleaned.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Failed to clean disk. Check the detailed output.", "Clean Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Show_disk_manager_btn_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(DISKMGMT_PATH);
        }
    }
}
