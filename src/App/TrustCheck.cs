// NetWatch - deciding whether a program looks unusual enough to be marked in red.
//
// This is NOT a virus scanner. It only looks at a few plain facts about the program file:
//   - does it carry a valid digital signature of its maker? (checked by Windows, without going online)
//   - does it run from a throw-away place such as a temporary or download folder?
//   - does the file say who made it and what it is?
//   - does it use the name of a Windows program without living in the Windows folder?
// A program is marked when it uses a Windows name from the wrong place, when its signature is broken,
// or when it is unsigned AND (runs from a throw-away folder OR does not say what it is).
// Plenty of harmless programs are unsigned, so "unsigned" alone is not enough to be marked.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace NetWatch
{
    internal enum SignatureState { Signed, Unsigned, Broken, Unknown }

    internal static class TrustCheck
    {
        static readonly string[] WindowsNames = new string[] {
            "svchost.exe", "lsass.exe", "csrss.exe", "services.exe", "winlogon.exe", "smss.exe", "wininit.exe",
            "explorer.exe", "taskhostw.exe", "dllhost.exe", "conhost.exe", "rundll32.exe", "spoolsv.exe",
            "dwm.exe", "ctfmon.exe", "sihost.exe", "runtimebroker.exe", "searchindexer.exe", "taskmgr.exe"
        };

        /// <summary>
        /// Returns null when nothing is unusual, otherwise the reasons (one per line).
        /// Call from a background thread: checking a signature reads the whole file.
        /// </summary>
        public static string Assess(string path, string description, string company, string windowsDir)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string fileName = Path.GetFileName(path);
            bool inWindows = IsInside(path, windowsDir) && !IsInside(path, Path.Combine(windowsDir, "Temp"));
            List<string> reasons = new List<string>();

            if (!inWindows)
            {
                foreach (string name in WindowsNames)
                {
                    if (!string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase)) continue;
                    reasons.Add("It uses the name of a Windows program (" + name + ") but is not in the Windows folder.");
                    break;
                }
            }

            // Windows' own files and Store apps are vouched for in a different way (a catalogue), so the
            // per-file check below would wrongly call them unsigned.
            bool storeApp = path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!inWindows && !storeApp)
            {
                SignatureState signature = CheckSignature(path);
                if (signature == SignatureState.Broken)
                {
                    reasons.Add("Its digital signature is broken: the file was changed after its maker signed it.");
                }
                else if (signature == SignatureState.Unsigned)
                {
                    bool throwAway = IsThrowAwayPlace(path, windowsDir);
                    bool anonymous = string.IsNullOrEmpty(company) && string.IsNullOrEmpty(description);
                    if (throwAway || anonymous || reasons.Count > 0)
                    {
                        reasons.Add("It is not digitally signed, so nobody vouches for who made it.");
                        if (throwAway) reasons.Add("It runs from a temporary or download folder.");
                        if (anonymous) reasons.Add("The file does not say what it is or who made it.");
                    }
                }
            }
            return reasons.Count == 0 ? null : string.Join("\n", reasons.ToArray());
        }

        static bool IsInside(string path, string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            return path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsThrowAwayPlace(string path, string windowsDir)
        {
            try
            {
                if (IsInside(path, Path.GetTempPath())) return true;
            }
            catch
            {
            }
            if (IsInside(path, Path.Combine(windowsDir, "Temp"))) return true;
            string[] markers = new string[] { "\\AppData\\Local\\Temp\\", "\\Downloads\\", "\\Users\\Public\\", "\\$Recycle.Bin\\" };
            foreach (string marker in markers)
                if (path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ---- asking Windows whether the file's signature is valid (WinVerifyTrust), strictly offline

        const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
        const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
        const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
        const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_FILE_INFO
        {
            public int cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public int cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public int dwUIChoice;
            public int fdwRevocationChecks;
            public int dwUnionChoice;
            public IntPtr pFile;
            public int dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public int dwProvFlags;
            public int dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WINTRUST_DATA data);

        public static SignatureState CheckSignature(string path)
        {
            IntPtr pathPtr = IntPtr.Zero, filePtr = IntPtr.Zero;
            try
            {
                if (!File.Exists(path)) return SignatureState.Unknown;
                pathPtr = Marshal.StringToHGlobalUni(path);
                WINTRUST_FILE_INFO file = new WINTRUST_FILE_INFO();
                file.cbStruct = Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
                file.pcwszFilePath = pathPtr;
                filePtr = Marshal.AllocHGlobal(file.cbStruct);
                Marshal.StructureToPtr(file, filePtr, false);

                WINTRUST_DATA data = new WINTRUST_DATA();
                data.cbStruct = Marshal.SizeOf(typeof(WINTRUST_DATA));
                data.dwUIChoice = 2;                    // WTD_UI_NONE: never show a window
                data.fdwRevocationChecks = 0;           // WTD_REVOKE_NONE
                data.dwUnionChoice = 1;                 // WTD_CHOICE_FILE
                data.pFile = filePtr;
                data.dwStateAction = 0;                 // WTD_STATEACTION_IGNORE: nothing to clean up afterwards
                data.dwProvFlags = 0x1000 | 0x10;       // cache only (no network) | no revocation check

                Guid action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");    // WINTRUST_ACTION_GENERIC_VERIFY_V2
                int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                if (result == 0) return SignatureState.Signed;
                if (result == TRUST_E_NOSIGNATURE || result == TRUST_E_SUBJECT_FORM_UNKNOWN || result == TRUST_E_PROVIDER_UNKNOWN)
                    return SignatureState.Unsigned;
                if (result == TRUST_E_BAD_DIGEST) return SignatureState.Broken;
                // Signed, but Windows could not fully confirm the certificate offline (expired, unknown issuer ...):
                // not enough to call the program unusual.
                return SignatureState.Unknown;
            }
            catch
            {
                return SignatureState.Unknown;
            }
            finally
            {
                if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
                if (pathPtr != IntPtr.Zero) Marshal.FreeHGlobal(pathPtr);
            }
        }
    }
}
