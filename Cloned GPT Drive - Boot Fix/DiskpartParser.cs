using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Cloned_GPT_Drive___Boot_Fix
{
    /// <summary>
    /// Represents a single volume as parsed from 'diskpart list volume' or 'detail disk' output.
    /// </summary>
    internal class VolumeEntry
    {
        public int Number;
        public string Letter = "";      // may be empty if no letter assigned
        public string Label = "";
        public string FileSystem = "";
        public string Size = "";
        public string Status = "";      // e.g. "Healthy"
        public string Info = "";        // e.g. "System", "Boot", "Hidden"
        public int? DiskNumber;         // physical disk this volume lives on (null if unknown)

        public string DiskpartSelector => "Volume " + Number;
    }

    /// <summary>
    /// Represents a single disk row from 'diskpart list disk' output.
    /// </summary>
    internal class DiskListEntry
    {
        public int Number;
        public string Status = "";      // e.g. "Online", "Offline", "No Media"
        public string Size = "";
        public string Free = "";
        public bool IsGpt;
    }

    /// <summary>
    /// Everything 'select disk N' + 'detail disk' reported about one disk.
    /// </summary>
    internal class DiskDetail
    {
        public int Number;
        public bool Selected;           // diskpart confirmed "Disk N is now the selected disk"
        public bool DetailFound;        // the 'detail disk' block (Disk ID line) was present
        public string Model = "";
        public string DiskId = "";
        public string BusType = "";     // "Type" line, e.g. SATA, NVMe, USB
        public string LocationPath = "";    // physical connection, e.g. PCIROOT(0)#PCI(1700)#ATA(C01T00L00)
        public bool IsBootDisk;
        public bool IsPagefileDisk;
        public bool IsHibernationDisk;
        public bool IsCrashdumpDisk;
        public bool NoVolumes;          // diskpart said "There are no volumes."
        public bool VolumeTableFound;
        public List<VolumeEntry> Volumes = new List<VolumeEntry>();

        /// <summary>
        /// True only when the output proves which disk it describes and fully lists its volumes.
        /// Anything less means protection cannot be verified, so callers must refuse to continue.
        /// </summary>
        public bool IsComplete => Selected && DetailFound && (VolumeTableFound || NoVolumes);

        /// <summary>
        /// True when the running Windows installation depends on this disk: it boots from it,
        /// pages to it, or one of its volumes is the System/Boot/Pagefile volume.
        /// </summary>
        public bool IsInUseByWindows =>
            IsBootDisk || IsPagefileDisk || IsHibernationDisk || IsCrashdumpDisk
            || Volumes.Any(v => Regex.IsMatch(v.Info, @"\b(System|Boot|Pagefile|Hibernation|Crashdump)\b", RegexOptions.IgnoreCase));

        public List<string> Letters => Volumes
            .Where(v => !string.IsNullOrEmpty(v.Letter))
            .Select(v => v.Letter + ":\\")
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// A protected physical disk as stored in settings: its hardware key (see
    /// DiskpartParser.BuildDiskKey), the Disk ID it had when protected, and a name to show.
    /// Only the hardware key decides protection — a clone copies the Disk ID, so matching on
    /// it would tie the clone and its source together.
    /// </summary>
    internal class ProtectedDiskEntry
    {
        public string Key = "";
        public string DiskId = "";
        public string Name = "";

        /// <summary>
        /// The serial number part of the key, or "" if the key was built without one.
        /// </summary>
        public string SerialNumber
        {
            get
            {
                string[] parts = Key.Split('|');
                return parts.Length == 3 && parts[1].StartsWith("SN:") ? parts[1].Substring(3) : "";
            }
        }

        /// <summary>
        /// The size part of the key, or 0 if it can't be read.
        /// </summary>
        public ulong SizeBytes
        {
            get
            {
                string[] parts = Key.Split('|');
                return parts.Length == 3 && ulong.TryParse(parts[2], out ulong size) ? size : 0;
            }
        }

        public override string ToString()
        {
            return string.Join("\t", Clean(Key), Clean(DiskId), Clean(Name));
        }

        public static ProtectedDiskEntry Parse(string stored)
        {
            string[] parts = (stored ?? "").Split('\t');
            return new ProtectedDiskEntry
            {
                Key = parts[0].Trim(),
                DiskId = parts.Length > 1 ? parts[1].Trim() : "",
                Name = parts.Length > 2 ? parts[2].Trim() : ""
            };
        }

        private static string Clean(string value)
        {
            return (value ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }

    /// <summary>
    /// Pure parsing of diskpart text output. No disk access happens in here, which keeps it
    /// safe to unit test on any machine.
    /// </summary>
    internal static class DiskpartParser
    {
        /// <summary>
        /// Parses every table whose header row starts with the given text (e.g. "Volume ###").
        /// Column positions are taken from the dashed separator line under the header, so cells
        /// containing spaces and empty cells parse correctly — a plain regex cannot tell
        /// "no letter + label" apart from "letter". The '*' marking the selected row is ignored.
        /// </summary>
        public static List<string[]> ParseTable(string diskpartOutput, string headerStart, string rowStart)
        {
            var rows = new List<string[]>();
            if (string.IsNullOrEmpty(diskpartOutput))
                return rows;

            List<Tuple<int, int>> columns = null;
            string[] lines = diskpartOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();

                // Header row: learn the column widths from the dashed separator beneath it
                if (trimmed.StartsWith(headerStart) && i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith("---"))
                {
                    columns = new List<Tuple<int, int>>();
                    string dashes = lines[i + 1];
                    int pos = 0;
                    while (pos < dashes.Length)
                    {
                        if (dashes[pos] == '-')
                        {
                            int start = pos;
                            while (pos < dashes.Length && dashes[pos] == '-') pos++;
                            columns.Add(Tuple.Create(start, pos - start));
                        }
                        else
                        {
                            pos++;
                        }
                    }
                    i++; // skip the dashed separator line
                    continue;
                }

                // A table ends at the first blank line, so later messages such as
                // "Disk 1 is now the selected disk." are never mistaken for rows
                if (trimmed.Length == 0)
                {
                    columns = null;
                    continue;
                }

                if (columns == null || !trimmed.TrimStart('*', ' ').StartsWith(rowStart))
                    continue;

                // The selected row is prefixed with '*'; blank it so the first cell slices cleanly
                string line = lines[i];
                int star = line.IndexOf('*');
                if (star >= 0 && star < columns[0].Item1 + columns[0].Item2 && line.Substring(0, star).Trim().Length == 0)
                    line = line.Substring(0, star) + " " + line.Substring(star + 1);

                rows.Add(columns.Select(c => SliceColumn(line, c)).ToArray());
            }

            return rows;
        }

        private static string SliceColumn(string line, Tuple<int, int> column)
        {
            if (column.Item1 >= line.Length)
                return "";
            return line.Substring(column.Item1, Math.Min(column.Item2, line.Length - column.Item1)).Trim();
        }

        /// <summary>
        /// Parses every volume table found in diskpart output (works for both 'list volume'
        /// and 'detail disk').
        /// </summary>
        public static List<VolumeEntry> ParseVolumeTable(string diskpartOutput)
        {
            var entries = new List<VolumeEntry>();

            foreach (string[] cells in ParseTable(diskpartOutput, "Volume ###", "Volume "))
            {
                if (cells.Length < 6)
                    continue;

                string numText = Regex.Match(cells[0], @"\d+").Value;
                if (numText.Length == 0)
                    continue;

                entries.Add(new VolumeEntry
                {
                    Number = int.Parse(numText),
                    Letter = cells[1].ToUpper(),
                    Label = cells[2],
                    FileSystem = cells[3].ToUpper(),
                    // cells[4] is Type (Partition/Removable/DVD-ROM) — not shown
                    Size = cells[5],
                    Status = cells.Length > 6 ? cells[6] : "",
                    Info = cells.Length > 7 ? cells[7] : ""
                });
            }

            return entries;
        }

        /// <summary>
        /// Parses the disk table from 'diskpart list disk' output.
        /// </summary>
        public static List<DiskListEntry> ParseDiskList(string diskpartOutput)
        {
            var entries = new List<DiskListEntry>();

            foreach (string[] cells in ParseTable(diskpartOutput, "Disk ###", "Disk "))
            {
                if (cells.Length < 4)
                    continue;

                string numText = Regex.Match(cells[0], @"\d+").Value;
                if (numText.Length == 0 || entries.Any(d => d.Number == int.Parse(numText)))
                    continue;

                entries.Add(new DiskListEntry
                {
                    Number = int.Parse(numText),
                    Status = cells[1],
                    Size = cells[2],
                    Free = cells[3],
                    IsGpt = cells.Length > 5 && cells[5].Contains("*")
                });
            }

            return entries;
        }

        /// <summary>
        /// Parses the output of 'select disk N' followed by 'detail disk' for one disk.
        /// </summary>
        public static DiskDetail ParseDiskDetail(string diskpartOutput, int diskNumber)
        {
            var detail = new DiskDetail { Number = diskNumber };
            if (string.IsNullOrEmpty(diskpartOutput))
                return detail;

            detail.Selected = Regex.IsMatch(diskpartOutput, $@"\bDisk {diskNumber} is now the selected disk\.", RegexOptions.IgnoreCase);

            string[] lines = diskpartOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = StripPrompt(lines[i]);

                Match id = Regex.Match(line, @"^Disk ID\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (id.Success)
                {
                    detail.DetailFound = true;
                    detail.DiskId = id.Groups[1].Value.Trim();

                    // The model name is the last non-empty line printed right before "Disk ID"
                    for (int j = i - 1; j >= 0; j--)
                    {
                        string previous = StripPrompt(lines[j]);
                        if (previous.Length > 0)
                        {
                            if (!previous.EndsWith("is now the selected disk."))
                                detail.Model = previous;
                            break;
                        }
                    }
                    continue;
                }

                Match busType = Regex.Match(line, @"^Type\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (busType.Success && detail.DetailFound && detail.BusType.Length == 0)
                    detail.BusType = busType.Groups[1].Value.Trim();

                Match location = Regex.Match(line, @"^Location Path\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (location.Success && detail.DetailFound && detail.LocationPath.Length == 0)
                    detail.LocationPath = location.Groups[1].Value.Trim();

                if (IsYesFlag(line, "Boot Disk")) detail.IsBootDisk = true;
                if (IsYesFlag(line, "Pagefile Disk")) detail.IsPagefileDisk = true;
                if (IsYesFlag(line, "Hibernation File Disk")) detail.IsHibernationDisk = true;
                if (IsYesFlag(line, "Crashdump Disk")) detail.IsCrashdumpDisk = true;

                if (line.StartsWith("There are no volumes", StringComparison.OrdinalIgnoreCase))
                    detail.NoVolumes = true;

                if (line.StartsWith("Volume ###") && i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith("---"))
                    detail.VolumeTableFound = true;
            }

            detail.Volumes = ParseVolumeTable(diskpartOutput);
            return detail;
        }

        /// <summary>
        /// Splits the output of several 'select disk N' + 'detail disk' pairs run in one diskpart
        /// session into one section per disk number. A disk whose select failed gets no section.
        /// </summary>
        public static Dictionary<int, string> SplitDiskSections(string diskpartOutput)
        {
            var sections = new Dictionary<int, string>();
            if (string.IsNullOrEmpty(diskpartOutput))
                return sections;

            var matches = Regex.Matches(diskpartOutput, @"\bDisk (\d+) is now the selected disk\.", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .ToList();

            for (int i = 0; i < matches.Count; i++)
            {
                int diskNumber = int.Parse(matches[i].Groups[1].Value);
                int start = matches[i].Index;
                int end = (i + 1 < matches.Count) ? matches[i + 1].Index : diskpartOutput.Length;

                // A disk selected twice would be ambiguous; drop it rather than guess
                if (sections.ContainsKey(diskNumber))
                    sections[diskNumber] = "";
                else
                    sections[diskNumber] = diskpartOutput.Substring(start, end - start);
            }

            return sections;
        }

        /// <summary>
        /// Reads one Win32_LogicalDiskToPartition link: the partition side names the disk
        /// ("...Win32_DiskPartition.DeviceID="Disk #1, Partition #2"") and the logical side the
        /// letter ("...Win32_LogicalDisk.DeviceID="E:""). Returns false if either can't be read.
        /// </summary>
        public static bool TryParseLetterToDiskLink(string antecedent, string dependent, out string letter, out int diskNumber)
        {
            letter = "";
            diskNumber = -1;

            Match disk = Regex.Match(antecedent ?? "", @"Disk #(\d+), Partition #\d+");
            Match drive = Regex.Match(dependent ?? "", @"DeviceID=""([A-Za-z]):""");
            if (!disk.Success || !drive.Success)
                return false;

            letter = drive.Groups[1].Value.ToUpper() + ":\\";
            diskNumber = int.Parse(disk.Groups[1].Value);
            return true;
        }

        /// <summary>
        /// True for a Disk ID that actually identifies a disk — not empty, and not the all-zero
        /// signature/GUID of an empty or uninitialised disk.
        /// </summary>
        public static bool IsMeaningfulDiskId(string diskId)
        {
            return !string.IsNullOrEmpty(diskId) && Regex.IsMatch(diskId, "[1-9A-Fa-f]");
        }

        /// <summary>
        /// True if diskpart confirmed that volume N is the selected volume.
        /// </summary>
        public static bool IsVolumeSelected(string diskpartOutput, int volumeNumber)
        {
            return !string.IsNullOrEmpty(diskpartOutput)
                && Regex.IsMatch(diskpartOutput, $@"\bVolume {volumeNumber} is the selected volume\.", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Checks the output of 'select disk N' + 'detail disk', read in the same diskpart session
        /// that is about to run a destructive command, against the details confirmed by the user.
        /// The disk must be complete, have the same Disk ID, model, connection (bus type and
        /// location path), volumes and letters, and must not be in use by the running Windows.
        /// The connection is what tells apart two same-model blank disks with the same empty ID.
        /// </summary>
        public static bool IsSameDiskAsConfirmed(string diskpartOutput, int diskNumber, DiskDetail confirmed)
        {
            if (confirmed == null || !confirmed.IsComplete)
                return false;

            DiskDetail current = ParseDiskDetail(diskpartOutput, diskNumber);
            return current.IsComplete
                && !current.IsInUseByWindows
                && string.Equals(current.DiskId, confirmed.DiskId, StringComparison.OrdinalIgnoreCase)
                && current.Model == confirmed.Model
                && current.BusType == confirmed.BusType
                && current.LocationPath == confirmed.LocationPath
                && current.Volumes.Select(v => v.Number + "|" + v.Letter + "|" + v.FileSystem + "|" + v.Size)
                    .SequenceEqual(confirmed.Volumes.Select(v => v.Number + "|" + v.Letter + "|" + v.FileSystem + "|" + v.Size));
        }

        private static string StripPrompt(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("DISKPART>"))
                trimmed = trimmed.Substring("DISKPART>".Length).Trim();
            return trimmed;
        }

        private static bool IsYesFlag(string line, string name)
        {
            return Regex.IsMatch(line, "^" + Regex.Escape(name) + @"\s*:\s*Yes\b", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Builds a stable identity for a physical disk from its hardware details. Disk letters,
        /// numbers and the partition-table Disk ID can all change or be copied by a clone; the
        /// model + serial number + exact size can not. Falls back to the PnP device path when
        /// the drive (or its USB bridge) reports no serial number.
        /// </summary>
        public static string BuildDiskKey(string model, string serialNumber, ulong sizeBytes, string pnpDeviceId)
        {
            string serial = (serialNumber ?? "").Trim();
            string hardwareId = serial.Length > 0 ? "SN:" + serial : "PNP:" + (pnpDeviceId ?? "").Trim();
            if (hardwareId == "PNP:")
                return "";
            return $"{(model ?? "").Trim()}|{hardwareId}|{sizeBytes}";
        }
    }
}
