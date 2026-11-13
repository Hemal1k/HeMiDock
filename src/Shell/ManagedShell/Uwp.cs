// Taken from ManagedShell (https://github.com/cairoshell/ManagedShell), Apache License 2.0; changed for HeMiDock to run
// without WPF and Windows Forms where needed (pictures as pixels, own dispatcher, timer and message window).
// The files of one area of ManagedShell joined into one (their names are kept as headings).

using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System;

// ----- UWPInterop\StoreApp.cs -----
namespace ManagedShell.UWPInterop
{
    public class StoreApp : IEquatable<StoreApp>
    {
        public readonly string AppUserModelId;
        public string DisplayName;
        public string EntryPoint;
        public string HostId;
        public string IconColor;

        public string SmallIconPath;
        public string MediumIconPath;
        public string LargeIconPath;
        public string ExtraLargeIconPath;
        public string JumboIconPath;

        public StoreApp(string appUserModelId)
        {
            AppUserModelId = appUserModelId;
        }

        /// <summary>
        /// HeMiDock: the icon as the shell has it (shell:AppsFolder\…, at its full size; the app shrinks it), read by the
        /// logic of HeMiDock instead of a WPF bitmap of the icon file.
        /// </summary>
        public ShellImage GetIcon(IconSize size)
        {
            try
            {
                if (HeMiDock.AppShell.Icon("shell:AppsFolder\\" + AppUserModelId) is { } icon)
                    return new ShellImage(icon.Pixels, icon.Width, icon.Height);
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"StoreApp: Unable to load icon for {DisplayName}: {e.Message}");
            }
            return IconImageConverter.GetDefaultIcon();
        }

        #region IEquitable
        public bool Equals(StoreApp other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return AppUserModelId == other.AppUserModelId;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((StoreApp) obj);
        }

        public override int GetHashCode()
        {
            return AppUserModelId.GetHashCode();
        }
        #endregion
    }
}

// ----- UWPInterop\StoreAppHelper.cs -----
namespace ManagedShell.UWPInterop
{
    public class StoreAppHelper
    {
        const string defaultColor = "#00111111";

        private static string userSID;
        private static double scale;

        public static StoreAppList AppList = new StoreAppList();


        private static StoreApp getAppFromNode(Windows.ApplicationModel.Package package, string packagePath,
            XmlNode appNode, XmlNamespaceManager xmlnsManager)
        {
            XmlNode appIdNode = appNode.SelectSingleNode("@Id", xmlnsManager);

            if (appIdNode == null)
            {
                return null;
            }

            Dictionary<IconSize, string> icons = getIcons(packagePath, appNode, xmlnsManager);

            StoreApp storeApp = new StoreApp(package.Id.FamilyName + "!" + appIdNode.Value)
            {
                DisplayName = getDisplayName(package.Id.Name, packagePath, appNode, xmlnsManager),
                SmallIconPath = icons[IconSize.Small],
                MediumIconPath = icons[IconSize.Medium],
                LargeIconPath = icons[IconSize.Large],
                ExtraLargeIconPath = icons[IconSize.ExtraLarge],
                JumboIconPath = icons[IconSize.Jumbo],
                IconColor = getPlateColor(icons[IconSize.Small], appNode, xmlnsManager),
                EntryPoint = getEntryPoint(appNode, xmlnsManager),
                HostId = getHostId(appNode, xmlnsManager)
            };

            return storeApp;
        }

        private static XmlDocument getManifest(string path)
        {
            XmlDocument manifest = new XmlDocument();
            string manPath = path + "\\AppxManifest.xml";

            if (ShellHelper.Exists(manPath))
                manifest.Load(manPath);

            return manifest;
        }

        private static XmlNamespaceManager getNamespaceManager(XmlDocument manifest)
        {
            XmlNamespaceManager xmlnsManager = new XmlNamespaceManager(manifest.NameTable);
            xmlnsManager.AddNamespace("ns", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
            xmlnsManager.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10");
            xmlnsManager.AddNamespace("uap2", "http://schemas.microsoft.com/appx/manifest/uap/windows10/2");
            xmlnsManager.AddNamespace("uap3", "http://schemas.microsoft.com/appx/manifest/uap/windows10/3");
            xmlnsManager.AddNamespace("uap4", "http://schemas.microsoft.com/appx/manifest/uap/windows10/4");
            xmlnsManager.AddNamespace("uap5", "http://schemas.microsoft.com/appx/manifest/uap/windows10/5");
            xmlnsManager.AddNamespace("uap10", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10");

            return xmlnsManager;
        }

        private static XmlNode getXmlnsNode(string nodeText, XmlNode app, XmlNamespaceManager xmlnsManager)
        {
            XmlNode node = app.SelectSingleNode(nodeText, xmlnsManager);

            if (node == null && nodeText.Contains("uap:"))
            {
                int i = 0;
                string[] namespaces = { "uap:", "uap2:", "uap3:", "uap4:", "uap5:", "uap10:" };
                while (node == null && i <= 3)
                {
                    nodeText = nodeText.Replace(namespaces[i], namespaces[i + 1]);
                    node = app.SelectSingleNode(nodeText, xmlnsManager);
                    i++;
                }
            }

            return node;
        }

        private static string getDisplayName(string packageName, string packagePath, XmlNode app, XmlNamespaceManager xmlnsManager)
        {
            XmlNode nameNode = getXmlnsNode("uap:VisualElements/@DisplayName", app, xmlnsManager);

            if (nameNode == null)
                return packageName;

            string nameKey = nameNode.Value;

            if (!Uri.TryCreate(nameKey, UriKind.Absolute, out var nameUri))
                return nameKey;

            var resourceKey = $"ms-resource://{packageName}/resources/{nameUri.Segments.Last()}";
            string name = ExtractStringFromPRIFile(packagePath + "\\resources.pri", resourceKey);
            if (!string.IsNullOrEmpty(name))
                return name;

            resourceKey = $"ms-resource://{packageName}/{nameUri.Segments.Last()}";
            name = ExtractStringFromPRIFile(packagePath + "\\resources.pri", resourceKey);
            if (!string.IsNullOrEmpty(name))
                return name;

            return ExtractStringFromPRIFile(packagePath + "\\resources.pri", nameUri.ToString());
        }

        private static string getEntryPoint(XmlNode app, XmlNamespaceManager xmlnsManager)
        {
            return app.SelectSingleNode("@EntryPoint", xmlnsManager)?.Value;
        }

        private static string getHostId(XmlNode app, XmlNamespaceManager xmlnsManager)
        {
            return app.SelectSingleNode("@uap10:HostId", xmlnsManager)?.Value;
        }

        private static string getPlateColor(string iconPath, XmlNode app, XmlNamespaceManager xmlnsManager)
        {
            if (iconPath.EndsWith("_altform-unplated.png"))
                return defaultColor;

            XmlNode colorKey = getXmlnsNode("uap:VisualElements/@BackgroundColor", app, xmlnsManager);

            if (colorKey != null && !string.IsNullOrEmpty(colorKey.Value) && colorKey.Value.ToLower() != "transparent")
                return colorKey.Value;

            return defaultColor;
        }

        private static Dictionary<IconSize, string> getIcons(string path, XmlNode app, XmlNamespaceManager xmlnsManager)
        {
            Dictionary<IconSize, string> icons = new Dictionary<IconSize, string>();
            XmlNode iconNode = getXmlnsNode("uap:VisualElements/@Square44x44Logo", app, xmlnsManager);

            if (iconNode == null)
                return icons;

            string iconPath = path + "\\" + (iconNode.Value).Replace(".png", "");

            // get all resources, then use the first match
            string[] files = Directory.GetFiles(path, "*.png", SearchOption.AllDirectories);
            string baseName = Path.GetFileNameWithoutExtension(iconPath + ".png").ToLower();

            icons[IconSize.Small] = getIconPath(files, baseName, IconSize.Small);
            icons[IconSize.Medium] = getIconPath(files, baseName, IconSize.Medium);
            icons[IconSize.Large] = getIconPath(files, baseName, IconSize.Large);
            icons[IconSize.ExtraLarge] = getIconPath(files, baseName, IconSize.ExtraLarge);
            icons[IconSize.Jumbo] = getIconPath(files, baseName, IconSize.Jumbo);

            return icons;
        }

        private static string getIconPath(string[] files, string baseName, IconSize size)
        {
            List<string> iconAssets = new List<string> {
                ".targetsize-32_altform-unplated.png",
                ".targetsize-32_altform-unplated_contrast-black.png",
                ".targetsize-36_altform-unplated.png",
                ".targetsize-36_altform-unplated_contrast-black.png",
                ".targetsize-40_altform-unplated.png",
                ".targetsize-40_altform-unplated_contrast-black.png",
                ".targetsize-48_altform-unplated.png",
                ".targetsize-48_altform-unplated_contrast-black.png",
                ".png",
                "_contrast-black.png",
                ".targetsize-32.png",
                ".targetsize-32_contrast-black.png",
                ".targetsize-36.png",
                ".targetsize-36_contrast-black.png",
                ".targetsize-40.png",
                ".targetsize-40_contrast-black.png",
                ".targetsize-44.png",
                ".targetsize-44_contrast-black.png",
                ".targetsize-48.png",
                ".targetsize-48_contrast-black.png",
                ".targetsize-256_altform-unplated.png",
                ".targetsize-256_altform-unplated_contrast-black.png",
                ".scale-200.png",
                ".scale-200_contrast-black.png",
                ".targetsize-24_altform-unplated.png",
                ".targetsize-24_altform-unplated_contrast-black.png",
                ".targetsize-16_altform-unplated.png",
                ".targetsize-16_altform-unplated_contrast-black.png",
                ".targetsize-24.png",
                ".targetsize-24_contrast-black.png",
                ".targetsize-16.png",
                ".targetsize-16_contrast-black.png",
                ".scale-100.png",
                ".scale-100_contrast-black.png",
                ".targetsize-256.png",
                ".targetsize-256_contrast-black.png"
            };

            // do some sorting based on DPI for prettiness
            if (scale == 0)
                scale = DpiHelper.DpiScale;

            int numMoved = 0;
            for (int i = 0; i < iconAssets.Count; i++)
            {
                if ((scale < 1.25 && size == IconSize.Small && iconAssets[i].Contains("16")) ||
                    (((scale >= 1.25 && scale < 1.75 && size == IconSize.Small) || (scale < 1.25 && size == IconSize.Medium)) && iconAssets[i].Contains("24")) ||
                    (((scale >= 1.5 && size == IconSize.Medium) || (scale >= 1.25 && scale <= 1.75 && size == 0)) && iconAssets[i].Contains("48")) ||
                    (((scale >= 1.5 && size != IconSize.Small) || (scale >= 1.25 && size == 0) || size == IconSize.Jumbo) && (iconAssets[i].Contains("200") || iconAssets[i].Contains("100") || iconAssets[i].Contains("256"))))
                {
                    string copy = iconAssets[i];
                    iconAssets.RemoveAt(i);
                    iconAssets.Insert(numMoved, copy);
                    numMoved++;
                }
            }

            foreach (string iconName in iconAssets)
            {
                string fullName = baseName + iconName;

                foreach (string fileName in files)
                {
                    if (string.Equals(Path.GetFileName(fileName), fullName, StringComparison.OrdinalIgnoreCase) && File.Exists(fileName))
                    {
                        return fileName;
                    }
                }
            }

            return string.Empty;
        }

        private static IEnumerable<Windows.ApplicationModel.Package> getPackages(Windows.Management.Deployment.PackageManager pman, string packageFamilyName)
        {
            if (userSID == null)
            {
                userSID = System.Security.Principal.WindowsIdentity.GetCurrent().User?.ToString();
            }

            if (userSID == null)
            {
                return Enumerable.Empty<Windows.ApplicationModel.Package>();
            }

            try
            {
                if (string.IsNullOrEmpty(packageFamilyName))
                {
                    return pman.FindPackagesForUser(userSID);
                }
                
                return pman.FindPackagesForUser(userSID, packageFamilyName);
            }
            catch
            {
                return Enumerable.Empty<Windows.ApplicationModel.Package>();
            }
        }

        private static string getPackagePath(Windows.ApplicationModel.Package package)
        {
            string path = "";

            // need to catch a system-thrown exception...
            try
            {
                path = package.InstalledLocation.Path;
            }
            catch {}

            return path;
        }

        internal static StoreApp GetStoreApp(string appUserModelId)
        {
            string[] pkgAppId = appUserModelId.Split('!');
            string packageFamilyName = "";
            string appId = "";

            if (pkgAppId.Count() > 1)
            {
                packageFamilyName = pkgAppId[0];
                appId = pkgAppId[1];
            }
            else
            {
                return null;
            }

            foreach (Windows.ApplicationModel.Package package in getPackages(new Windows.Management.Deployment.PackageManager(), packageFamilyName))
            {
                string packagePath = getPackagePath(package);

                if (string.IsNullOrEmpty(packagePath))
                {
                    continue;
                }

                XmlDocument manifest = getManifest(packagePath);
                XmlNamespaceManager xmlnsManager = getNamespaceManager(manifest);
                XmlNodeList appNodeList =
                    manifest.SelectNodes("/ns:Package/ns:Applications/ns:Application", xmlnsManager);

                if (appNodeList == null)
                {
                    return null;
                }

                foreach (XmlNode appNode in appNodeList)
                {
                    // get specific app in package
                    
                    if (appNode.SelectSingleNode("@Id", xmlnsManager)?.Value == appId)
                    {
                        // return values
                        return getAppFromNode(package, packagePath, appNode, xmlnsManager);
                    }
                }
            }

            return null;
        }

        internal static string ExtractStringFromPRIFile(string pathToPRI, string resourceKey)
        {
            string sWin8ManifestString = $"@{{{pathToPRI}? {resourceKey}}}";
            var outBuff = new StringBuilder(256);
            int result = Interop.NativeMethods.SHLoadIndirectString(sWin8ManifestString, outBuff, outBuff.Capacity, IntPtr.Zero);
            return outBuff.ToString();
        }
    }
}

// ----- UWPInterop\StoreAppList.cs -----
namespace ManagedShell.UWPInterop
{
    public partial class StoreAppList : IEnumerable<StoreApp>
    {
        private List<StoreApp> _appList = new List<StoreApp>();


        public StoreApp GetAppByAumid(string appUserModelId)
        {
            // first attempt to get an app in our list already
            foreach (var storeApp in _appList)
            {
                if (storeApp.AppUserModelId == appUserModelId)
                {
                    return storeApp;
                }
            }

            // not in list, get from StoreAppHelper
            StoreApp app = StoreAppHelper.GetStoreApp(appUserModelId);

            if (app != null)
            {
                _appList.Add(app);
                return app;
            }

            // no app found for given AUMID
            return null;
        }

        public IEnumerator<StoreApp> GetEnumerator()
        {
            return ((IEnumerable<StoreApp>)_appList).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)_appList).GetEnumerator();
        }
    }
}
