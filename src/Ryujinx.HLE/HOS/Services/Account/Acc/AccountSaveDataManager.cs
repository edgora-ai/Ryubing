using Gommon;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.HOS.Services.Account.Acc.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ryujinx.HLE.HOS.Services.Account.Acc
{
    public class AccountSaveDataManager
    {
        // Resolve at the access boundary, never when this type is first touched before root selection.
        private static string ProfilesJsonPath => AppDataManager.ValidatePathWithinRoot(
            Path.Join(AppDataManager.BaseDirPath, "system", "Profiles.json"));

        private static readonly ProfilesJsonSerializerContext _serializerContext = new(JsonHelper.GetDefaultSerializerOptions());

        public UserId LastOpened { get; set; }

        public AccountSaveDataManager(ConcurrentDictionary<string, UserProfile> profiles)
        {
            // TODO: Use 0x8000000000000010 system savedata instead of a JSON file if needed.

            if (File.Exists(ProfilesJsonPath))
            {
                try
                {
                    ProfilesJson profilesJson = JsonHelper.DeserializeFromFile(ProfilesJsonPath, _serializerContext.ProfilesJson);

                    foreach (UserProfileJson profile in profilesJson.Profiles)
                    {
                        UserProfile addedProfile = new(new UserId(profile.UserId), profile.Name, profile.Image, profile.LastModifiedTimestamp);

                        profiles.AddOrUpdate(profile.UserId, addedProfile, (key, old) => addedProfile);
                    }

                    LastOpened = new UserId(profilesJson.LastOpened);
                }
                catch (Exception ex) when (ex is JsonException or FormatException)
                {
                    Logger.Error?.Print(LogClass.Application, $"Failed to parse {ProfilesJsonPath}: {ex.Message} Loading default profile!");

                    LastOpened = AccountManager.DefaultUserId;
                }
            }
            else
            {
                LastOpened = AccountManager.DefaultUserId;
            }
        }

        public static Optional<UserProfile> GetLastUsedUser()
        {
            string path = ProfilesJsonPath;
            ProfilesJson profilesJson;
            try
            {
                profilesJson = JsonHelper.DeserializeFromFile(path, _serializerContext.ProfilesJson);
            }
            catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // A fresh independent root has no last-used account. Do not consult any legacy profile.
                return Optional<UserProfile>.None;
            }

            return profilesJson.Profiles
                .FindFirst(profile => profile.UserId == profilesJson.LastOpened)
                .Convert(profileJson => new UserProfile(new UserId(profileJson.UserId), profileJson.Name,
                    profileJson.Image, profileJson.LastModifiedTimestamp));
        }

        public void Save(ConcurrentDictionary<string, UserProfile> profiles)
        {
            ProfilesJson profilesJson = new()
            {
                Profiles = [],
                LastOpened = LastOpened.ToString(),
            };

            foreach (KeyValuePair<string, UserProfile> profile in profiles)
            {
                profilesJson.Profiles.Add(new UserProfileJson()
                {
                    UserId = profile.Value.UserId.ToString(),
                    Name = profile.Value.Name,
                    AccountState = profile.Value.AccountState,
                    OnlinePlayState = profile.Value.OnlinePlayState,
                    LastModifiedTimestamp = profile.Value.LastModifiedTimestamp,
                    Image = profile.Value.Image,
                });
            }

            JsonHelper.SerializeToFile(ProfilesJsonPath, profilesJson, _serializerContext.ProfilesJson);
        }
    }
}
