namespace Microsoft.HockeyApp.Services.Device
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;

    using global::Windows.ApplicationModel.Core;
    using global::Windows.Devices.Enumeration.Pnp;
    using global::Windows.Graphics.Display;
    using global::Windows.Networking.Connectivity;
    using global::Windows.Security.Cryptography;
    using global::Windows.Security.Cryptography.Core;
    using global::Windows.Security.ExchangeActiveSyncProvisioning;
    using global::Windows.Storage.Streams;
    using global::Windows.System.Profile;
    using Extensibility.Implementation.Tracing;
    using Extensibility;

    /// <summary>
    /// The reader is platform specific and will contain different implementations for reading specific data based on the platform its running on.
    /// </summary>
    internal partial class DeviceService : IDeviceService
    {
        private const string ModelNameKey = "System.Devices.ModelName";
        private const string ManufacturerKey = "System.Devices.Manufacturer";
        private const string DisplayPrimaryCategoryKey = "{78C34FC8-104A-4ACA-9EA4-524D52996E57},97";
        private const string DeviceDriverKey = "{A8B865DD-2E3D-4094-AD97-E593A70C75D6}";
        private const string DeviceDriverVersionKey = DeviceDriverKey + ",3";
        private const string DeviceDriverProviderKey = DeviceDriverKey + ",9";
        private const string RootContainer = "{00000000-0000-0000-FFFF-FFFFFFFFFFFF}";
        private const string RootContainerQuery = "System.Devices.ContainerId:=\"" + RootContainer + "\"";

        /// <summary>
        /// The number of milliseconds to wait before asynchronously retrying an operation.
        /// </summary>
        private const int AsyncRetryIntervalInMilliseconds = 100;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeviceService"/> class.
        /// </summary>
        internal DeviceService()
        {
        }

#pragma warning disable 1998
        /// <summary>
        /// Get the device category this computer belongs to.
        /// </summary>
        /// <example>Computer.Desktop, Computer.Tablet.</example>
        /// <returns>The category of this device.</returns>
        public async virtual Task<string> GetDeviceType()
        {
#if WINDOWS_UWP
            return AnalyticsInfo.VersionInfo.DeviceFamily;
#elif WP8
            return "";
#else
            // WINRT
            var rootContainer = await PnpObject.CreateFromIdAsync(PnpObjectType.DeviceContainer, RootContainer, new[] { DisplayPrimaryCategoryKey });
            return (string)rootContainer.Properties[DisplayPrimaryCategoryKey];
#endif
        }
#pragma warning restore 1998

        /// <summary>
        /// Gets the device unique identifier.
        /// </summary>
        /// <returns>The discovered device identifier.</returns>
        public virtual string GetDeviceUniqueId()
        {
            string deviceId = null;
            try
            {
                // Per documentation here http://msdn.microsoft.com/en-us/library/windows/apps/jj553431.aspx we are selectively pulling out 
                // specific items from the hardware ID.
                StringBuilder builder = new StringBuilder();
                HardwareToken token = HardwareIdentification.GetPackageSpecificToken(null);
                using (DataReader dataReader = DataReader.FromBuffer(token.Id))
                {
                    int offset = 0;
                    while (offset < token.Id.Length)
                    {
                        // The first two bytes contain the type of the component and the next two bytes contain the value.
                        byte[] hardwareEntry = new byte[4];
                        dataReader.ReadBytes(hardwareEntry);

                        if ((hardwareEntry[0] == 1 || // CPU ID of the processor
                             hardwareEntry[0] == 2 || // Size of the memory
                             hardwareEntry[0] == 3 || // Serial number of the disk device
                             hardwareEntry[0] == 7 || // Mobile broadband ID
                             hardwareEntry[0] == 9) && // BIOS
                            hardwareEntry[1] == 0)
                        {
                            if (builder.Length > 0)
                            {
                                builder.Append(',');
                            }

                            builder.Append(hardwareEntry[2]);
                            builder.Append('_');
                            builder.Append(hardwareEntry[3]);
                        }

                        offset += 4;
                    }
                }

                // create a buffer containing the cleartext device ID
                IBuffer clearBuffer = CryptographicBuffer.ConvertStringToBinary(builder.ToString(), BinaryStringEncoding.Utf8);

                // get a provider for the SHA256 algorithm
                HashAlgorithmProvider hashAlgorithmProvider = HashAlgorithmProvider.OpenAlgorithm("SHA256");

                // hash the input buffer
                IBuffer hashedBuffer = hashAlgorithmProvider.HashData(clearBuffer);

                deviceId = CryptographicBuffer.EncodeToBase64String(hashedBuffer);
            }
            catch (Exception)
            {
                // For IoT sceanrios we will alwasy set the device id to IoT
                // Becuase HardwareIdentification API will always throw
                deviceId = "IoT";
            }

            return deviceId;
        }

#pragma warning disable 1998
        /// <summary>
        /// Gets the operating system version.
        /// </summary>
        /// <returns>The discovered operating system.</returns>
        public virtual async Task<string> GetOperatingSystemVersionAsync()
        {
#if WINDOWS_UWP
            // For UWP we are going with the new available API that must resolve memory issue described in a bug #566011.
            // Because for non-uwp we are enumerating all PNP objects, which requires ~2.8 MB.
            string sv = AnalyticsInfo.VersionInfo.DeviceFamilyVersion;
            ulong v = ulong.Parse(sv);
            return ConvertIntToVersion(v);
#else
            // WINRT

            // Getting OS Version for WinRT application is tricky. The Silverlight API using <see href="System.Environment.OSVersion" />
            // has been removed, but the new one <see href="AnalyticsInfo.VersionInfo.DeviceFamilyVersion" /> has been introduced only in a next 
            // version, Windows 10 UWP.

            // Therefore trick is the following:
            // For Windows Phone, try to get the version using reflection on top of AnalyticsInfo.VersionInfo.DeviceFamilyVersion, if that fails, 
            // return 8.1

            // For Windows 8.1 just use PnpObject which does its job. You can't use PnpObject for Windows Phone, it does not return correct value.
            return HockeyPlatformHelper81.Name == "HockeySDKWP81" ? GetOsVersionUsingAnalyticsInfo() : await GetOsVersionUsingPnpObject();
#endif
        }
#pragma warning restore 1998

        /// <summary>
        /// Converts integer to a <see cref="System.Version"/> format.
        /// </summary>
        /// <param name="v">Integer, that represents an OS version</param>
        /// <returns>Version string in format {0}.{1}.{2}.{3}</returns>
        internal static string ConvertIntToVersion(ulong v)
        {
            ulong v1 = (v & 0xFFFF000000000000L) >> 48;
            ulong v2 = (v & 0x0000FFFF00000000L) >> 32;
            ulong v3 = (v & 0x00000000FFFF0000L) >> 16;
            ulong v4 = (v & 0x000000000000FFFFL);
            string res = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}", v1, v2, v3, v4);
            return res;
        }

        private static string GetOsVersionUsingAnalyticsInfo()
        {
            const string DefaultWindows81Version = "8.1.0.0";
            var analyticsInfoType = Type.GetType("Windows.System.Profile.AnalyticsInfo, Windows, ContentType=WindowsRuntime");
            var versionInfoType = Type.GetType("Windows.System.Profile.AnalyticsVersionInfo, Windows, ContentType=WindowsRuntime");
            if (analyticsInfoType == null || versionInfoType == null)
            {
                // Apparently you are not on Windows 10, because AnalyticsInfo API was not available before Windows 10.
                return DefaultWindows81Version;
            }

            var versionInfoProperty = analyticsInfoType.GetRuntimeProperty("VersionInfo");
            object versionInfo = versionInfoProperty.GetValue(null);
            var versionProperty = versionInfoType.GetRuntimeProperty("DeviceFamilyVersion");
            object familyVersion = versionProperty.GetValue(versionInfo);

            ulong versionBytes;
            if (!ulong.TryParse(familyVersion.ToString(), out versionBytes))
            {
                return DefaultWindows81Version;
            }

            return ConvertIntToVersion(versionBytes);
        }

        private static async Task<string> GetOsVersionUsingPnpObject()
        {
            string[] requestedProperties = new string[]
                                   {
                                                   DeviceDriverVersionKey,
                                                   DeviceDriverProviderKey
                                   };

            PnpObjectCollection pnpObjects = await PnpObject.FindAllAsync(PnpObjectType.Device, requestedProperties, RootContainerQuery);

            string guessedVersion = pnpObjects.Select(item => new ProviderVersionPair
            {
                Provider = (string)GetValueOrDefault(item.Properties, DeviceDriverProviderKey),
                Version = (string)GetValueOrDefault(item.Properties, DeviceDriverVersionKey)
            })
                                              .Where(item => string.IsNullOrEmpty(item.Version) == false)
                                              .Where(item => string.Compare(item.Provider, "Microsoft", StringComparison.Ordinal) == 0)
                                              .GroupBy(item => item.Version)
                                              .OrderByDescending(item => item.Count())
                                              .Select(item => item.Key)
                                              .First();

            return guessedVersion;
        }


        /// <summary>
        /// Get the name of the manufacturer of this computer.
        /// </summary>
        /// <example>Microsoft Corporation.</example>
        /// <returns>The name of the manufacturer of this computer.</returns>
        public async Task<string> GetOemName()
        {
            var rootContainer = await PnpObject.CreateFromIdAsync(PnpObjectType.DeviceContainer, RootContainer, new[] { ManufacturerKey });
            return (string)rootContainer.Properties[ManufacturerKey];
        }

        /// <summary>
        /// Get the name of the model of this computer.
        /// </summary>
        /// <example>Precision WorkStation T7500.</example>
        /// <returns>The name of the model of this computer.</returns>
        public string GetDeviceModel()
        {
            // Inspired from https://www.suchan.cz/2015/08/uwp-quick-tip-getting-device-os-and-app-info/
            // on phones the SystemProductName contains the phone name in non-readable format like RM-940_nam_att_200. 
            // To convert this name to readable format, like Lumia 1520, we are using the mapping api on Breeze service.
            // All other API available like AnalyticsInfo.DeviceForm, PnpObject are not providing the expected values.
            // On Lumia 950 AnalyticsInfo.DeviceForm returns Unknown, PnpOjbect returns P6211
            return new EasClientDeviceInformation().SystemProductName;
        }

        /// <summary>
        /// Gets the network type.
        /// </summary>
        /// <returns>The discovered network type.</returns>
        public virtual int GetNetworkType()
        {
            int result;
            try
            {
                ConnectionProfile profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile == null || profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
                {
                    result = 0;
                }
                else
                {
                    result = (int)profile.NetworkAdapter.IanaInterfaceType;
                }
            }
            catch (Exception exception)
            {
                CoreEventSource.Log.LogVerbose("Fail reading Device network type: " + exception.ToString());
                result = 0;
            }

            return result;
        }

        /// <summary>
        /// Gets the host system locale.
        /// </summary>
        /// <returns>The discovered locale.</returns>
        public virtual string GetHostSystemLocale()
        {
            return CultureInfo.CurrentCulture.Name;
        }

        public string GetOperatingSystemName()
        {
            // currently SDK supports Windows only, so we are hardcoding this value.
            return "Windows";
        }
        

        private static TValue GetValueOrDefault<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> dictionary, TKey key)
        {
            TValue value;
            return dictionary.TryGetValue(key, out value) ? value : default(TValue);
        }

        private class ProviderVersionPair
        {
            public string Provider { get; set; }

            public string Version { get; set; }
        }
    }
}