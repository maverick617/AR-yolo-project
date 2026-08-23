using System;
using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Small PlayerPrefs-backed profile store. Profiles are keyed by the single
    /// physical tag attached to each cup.
    /// </summary>
    public sealed class CupRegistrationProfileStore : MonoBehaviour
    {
        [Serializable]
        private sealed class SavedProfiles
        {
            public List<CupRegistrationProfile> profiles = new List<CupRegistrationProfile>();
        }

        [SerializeField] private string storageKey = "ar80sretro.cup-profiles.v3";
        [SerializeField] private bool logProfileChanges = true;

        private readonly List<CupRegistrationProfile> profiles =
            new List<CupRegistrationProfile>();

        public IReadOnlyList<CupRegistrationProfile> Profiles => profiles;

        private void Awake()
        {
            Load();
        }

        public bool TryGetProfile(
            int tagId,
            string trackedImageName,
            out CupRegistrationProfile profile)
        {
            for (int i = 0; i < profiles.Count; i++)
            {
                CupRegistrationProfile candidate = profiles[i];
                if (candidate == null || !candidate.IsValid)
                {
                    continue;
                }

                bool idMatches = tagId >= 0 && candidate.TagId == tagId;
                bool imageMatches = !string.IsNullOrWhiteSpace(trackedImageName)
                    && string.Equals(
                        candidate.TrackedImageName,
                        trackedImageName,
                        StringComparison.OrdinalIgnoreCase);
                if (idMatches || imageMatches)
                {
                    profile = candidate;
                    return true;
                }
            }

            profile = null;
            return false;
        }

        public void SaveProfile(CupRegistrationProfile profile)
        {
            if (profile == null || !profile.IsValid)
            {
                Debug.LogWarning("Ignored invalid cup registration profile.", this);
                return;
            }

            for (int i = profiles.Count - 1; i >= 0; i--)
            {
                CupRegistrationProfile existing = profiles[i];
                if (existing == null)
                {
                    profiles.RemoveAt(i);
                    continue;
                }

                if ((profile.TagId >= 0 && existing.TagId == profile.TagId)
                    || (!string.IsNullOrWhiteSpace(profile.TrackedImageName)
                        && string.Equals(
                            existing.TrackedImageName,
                            profile.TrackedImageName,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    profiles.RemoveAt(i);
                }
            }

            profiles.Add(profile);
            Persist();

            if (logProfileChanges)
            {
                Debug.Log(
                    $"Cup profile saved: tag={profile.TagId}, "
                    + $"size={profile.MeasuredSizeMeters}, samples={profile.SampleCount}, "
                    + $"depth={profile.MeasuredWithEnvironmentDepth}.",
                    this);
            }
        }

        public void DeleteProfile(int tagId, string trackedImageName)
        {
            bool changed = false;
            for (int i = profiles.Count - 1; i >= 0; i--)
            {
                CupRegistrationProfile profile = profiles[i];
                if (profile == null)
                {
                    profiles.RemoveAt(i);
                    changed = true;
                    continue;
                }

                bool idMatches = tagId >= 0 && profile.TagId == tagId;
                bool imageMatches = !string.IsNullOrWhiteSpace(trackedImageName)
                    && string.Equals(
                        profile.TrackedImageName,
                        trackedImageName,
                        StringComparison.OrdinalIgnoreCase);
                if (!idMatches && !imageMatches)
                {
                    continue;
                }

                profiles.RemoveAt(i);
                changed = true;
            }

            if (changed)
            {
                Persist();
            }
        }

        [ContextMenu("Clear All Cup Registration Profiles")]
        public void ClearAllProfiles()
        {
            profiles.Clear();
            PlayerPrefs.DeleteKey(storageKey);
            PlayerPrefs.Save();
            Debug.Log("All cup registration profiles were cleared.", this);
        }

        private void Load()
        {
            profiles.Clear();
            if (!PlayerPrefs.HasKey(storageKey))
            {
                return;
            }

            try
            {
                SavedProfiles saved = JsonUtility.FromJson<SavedProfiles>(
                    PlayerPrefs.GetString(storageKey));
                if (saved?.profiles == null)
                {
                    return;
                }

                for (int i = 0; i < saved.profiles.Count; i++)
                {
                    CupRegistrationProfile profile = saved.profiles[i];
                    if (profile != null && profile.IsValid)
                    {
                        profiles.Add(profile);
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Could not load cup profiles: {exception.Message}", this);
            }
        }

        private void Persist()
        {
            SavedProfiles saved = new SavedProfiles();
            saved.profiles.AddRange(profiles);
            PlayerPrefs.SetString(storageKey, JsonUtility.ToJson(saved));
            PlayerPrefs.Save();
        }
    }
}
