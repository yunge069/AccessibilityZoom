#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>

#include "adlx/ADLXHelper/ADLXHelper.h"
#include "adlx/Include/IDisplayGamma.h"
#include "adlx/Include/IDisplaySettings.h"
#include "adlx/Include/IDisplays.h"
#include "adlx/Include/IDesktops.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <exception>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <vector>

using namespace adlx;

#define BRIDGE_EXPORT extern "C" __declspec(dllexport)

#pragma pack(push, 4)
struct AmdColorCapabilities
{
    int displayCount;
    int fullFilterDisplayCount;
    int gammaDisplayCount;
    int customColorDisplayCount;
    int brightnessDisplayCount;
    int contrastDisplayCount;
    int saturationDisplayCount;
    int reserved;
};

struct AmdColorSettings
{
    float gamma;
    float brightness;
    float contrast;
    float saturation;
    float shadowBoost;
    float redGain;
    float greenGain;
    float blueGain;
};
#pragma pack(pop)

namespace
{
    constexpr std::uint32_t RecoveryMagic = 0x435A4441; // ADZC
    constexpr std::uint32_t RecoveryVersion = 3;
    constexpr std::uint32_t MaxRecoveryDisplays = 32;

    enum GammaMode : std::int32_t
    {
        GammaDefault = 0,
        GammaReRamp = 1,
        GammaDeRamp = 2,
        GammaCoefficient = 3,
        GammaSrgb = 4,
        GammaBt709 = 5,
        GammaPq = 6,
        GammaPq2084Interim = 7,
        Gamma36 = 8
    };

    enum CustomFlags : std::uint32_t
    {
        HasBrightness = 1u << 0,
        HasContrast = 1u << 1,
        HasSaturation = 1u << 2,
        HasHue = 1u << 3,
        HasTemperature = 1u << 4
    };

#pragma pack(push, 1)
    struct RecoveryHeader
    {
        std::uint32_t magic;
        std::uint32_t version;
        std::uint32_t displayCount;
        std::uint32_t reserved;
    };

    struct RecoveryEntry
    {
        std::uint64_t uniqueId;
        std::uint32_t customFlags;
        std::int32_t brightness;
        std::int32_t contrast;
        std::int32_t saturation;
        std::int32_t hue;
        std::int32_t temperature;
        std::int32_t gammaMode;
        ADLX_RegammaCoeff coefficient;
        ADLX_GammaRamp ramp;
        std::uint32_t appliedValid;
        std::int32_t appliedBrightness;
        std::int32_t appliedContrast;
        std::int32_t appliedSaturation;
        ADLX_GammaRamp appliedRamp;
        std::uint32_t previousAppliedValid;
        std::int32_t previousAppliedBrightness;
        std::int32_t previousAppliedContrast;
        std::int32_t previousAppliedSaturation;
        ADLX_GammaRamp previousAppliedRamp;
    };
#pragma pack(pop)

    struct DisplayEntry
    {
        IADLXDisplayPtr display;
        std::uint64_t uniqueId = 0;
        bool gamma = false;
        bool customColor = false;
        bool brightness = false;
        bool contrast = false;
        bool saturation = false;
        RecoveryEntry snapshot = {};
        bool snapshotValid = false;
    };

    std::mutex g_mutex;
    IADLXDisplayServicesPtr g_displayServices;
    std::vector<DisplayEntry> g_displays;
    std::wstring g_lastError;
    std::wstring g_recoveryPath;
    bool g_initialized = false;
    bool g_adlxStarted = false;
    bool g_sessionActive = false;
    bool g_recoveryBlocked = false;
    bool g_recoveryPreservedExternal = false;

    bool Succeeded(ADLX_RESULT result)
    {
        return ADLX_SUCCEEDED(result);
    }

    std::uint32_t Crc32(const void* data, std::size_t size)
    {
        const auto* bytes = static_cast<const std::uint8_t*>(data);
        std::uint32_t crc = 0xFFFFFFFFu;
        for (std::size_t index = 0; index < size; ++index)
        {
            crc ^= bytes[index];
            for (int bit = 0; bit < 8; ++bit)
                crc = (crc >> 1) ^ (0xEDB88320u &
                    (0u - static_cast<std::uint32_t>(crc & 1u)));
        }
        return ~crc;
    }

    bool FileExists(const std::wstring& path)
    {
        if (path.empty())
            return false;
        std::error_code error;
        return std::filesystem::exists(std::filesystem::path(path), error) && !error;
    }

    void SetError(const std::wstring& message)
    {
        g_lastError = message;
    }

    void SetAdlxError(const wchar_t* operation, ADLX_RESULT result)
    {
        g_lastError = operation;
        g_lastError += L" failed (ADLX result ";
        g_lastError += std::to_wstring(static_cast<int>(result));
        g_lastError += L").";
    }

    bool GetCustomColor(const DisplayEntry& entry, IADLXDisplayCustomColorPtr& color)
    {
        color = nullptr;
        return g_displayServices != nullptr &&
            Succeeded(g_displayServices->GetCustomColor(entry.display, &color)) &&
            color != nullptr;
    }

    bool GetGamma(const DisplayEntry& entry, IADLXDisplayGammaPtr& gamma)
    {
        gamma = nullptr;
        return g_displayServices != nullptr &&
            Succeeded(g_displayServices->GetGamma(entry.display, &gamma)) &&
            gamma != nullptr;
    }

    bool CaptureSnapshot(DisplayEntry& entry)
    {
        RecoveryEntry snapshot = {};
        snapshot.uniqueId = entry.uniqueId;

        IADLXDisplayCustomColorPtr color;
        if (GetCustomColor(entry, color))
        {
            adlx_int value = 0;
            adlx_bool supported = false;

            if (Succeeded(color->IsBrightnessSupported(&supported)) && supported &&
                Succeeded(color->GetBrightness(&value)))
            {
                snapshot.customFlags |= HasBrightness;
                snapshot.brightness = value;
            }

            supported = false;
            if (Succeeded(color->IsContrastSupported(&supported)) && supported &&
                Succeeded(color->GetContrast(&value)))
            {
                snapshot.customFlags |= HasContrast;
                snapshot.contrast = value;
            }

            supported = false;
            if (Succeeded(color->IsSaturationSupported(&supported)) && supported &&
                Succeeded(color->GetSaturation(&value)))
            {
                snapshot.customFlags |= HasSaturation;
                snapshot.saturation = value;
            }

            // Hue and temperature are not owned by this feature, so they are
            // intentionally neither changed nor restored.
        }

        IADLXDisplayGammaPtr gamma;
        bool hasGamma = GetGamma(entry, gamma);
        bool gammaCaptured = false;
        if (hasGamma)
        {
            adlx_bool current = false;
            ADLX_RESULT stateResult = gamma->IsCurrentReGammaRamp(&current);
            if (!Succeeded(stateResult))
            {
                hasGamma = false;
            }
            else if (current)
            {
                gammaCaptured = Succeeded(gamma->GetGammaRamp(&snapshot.ramp));
                if (gammaCaptured)
                    snapshot.gammaMode = GammaReRamp;
            }
            else
            {
                current = false;
                stateResult = gamma->IsCurrentDeGammaRamp(&current);
                if (!Succeeded(stateResult))
                {
                    hasGamma = false;
                }
                else if (current)
                {
                    gammaCaptured = Succeeded(gamma->GetGammaRamp(&snapshot.ramp));
                    if (gammaCaptured)
                        snapshot.gammaMode = GammaDeRamp;
                }
                else
                {
                    current = false;
                    stateResult = gamma->IsCurrentRegammaCoefficient(&current);
                    if (!Succeeded(stateResult))
                    {
                        hasGamma = false;
                    }
                    else if (current)
                    {
                        gammaCaptured = Succeeded(
                            gamma->GetGammaCoefficient(&snapshot.coefficient));
                        if (gammaCaptured)
                            snapshot.gammaMode = GammaCoefficient;
                    }
                    else
                    {
                        current = false;
                        stateResult = gamma->IsCurrentReGammaSRGB(&current);
                        if (!Succeeded(stateResult))
                            hasGamma = false;
                        else if (current)
                        {
                            snapshot.gammaMode = GammaSrgb;
                            gammaCaptured = true;
                        }
                        else
                        {
                            current = false;
                            stateResult = gamma->IsCurrentReGammaBT709(&current);
                            if (!Succeeded(stateResult))
                                hasGamma = false;
                            else if (current)
                            {
                                snapshot.gammaMode = GammaBt709;
                                gammaCaptured = true;
                            }
                            else
                            {
                                current = false;
                                stateResult = gamma->IsCurrentReGammaPQ(&current);
                                if (!Succeeded(stateResult))
                                    hasGamma = false;
                                else if (current)
                                {
                                    snapshot.gammaMode = GammaPq;
                                    gammaCaptured = true;
                                }
                                else
                                {
                                    current = false;
                                    stateResult = gamma->IsCurrentReGammaPQ2084Interim(&current);
                                    if (!Succeeded(stateResult))
                                        hasGamma = false;
                                    else if (current)
                                    {
                                        snapshot.gammaMode = GammaPq2084Interim;
                                        gammaCaptured = true;
                                    }
                                    else
                                    {
                                        current = false;
                                        stateResult = gamma->IsCurrentReGamma36(&current);
                                        if (!Succeeded(stateResult))
                                            hasGamma = false;
                                        else
                                        {
                                            snapshot.gammaMode = current
                                                ? Gamma36
                                                : GammaDefault;
                                            gammaCaptured = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        entry.snapshot = snapshot;
        entry.snapshotValid = hasGamma && gammaCaptured &&
            (snapshot.customFlags & HasBrightness) != 0 &&
            (snapshot.customFlags & HasContrast) != 0 &&
            (snapshot.customFlags & HasSaturation) != 0;
        if (!entry.snapshotValid)
            SetError(L"AMD ADLX could not snapshot every required display-color value.");
        return entry.snapshotValid;
    }

    bool IsGammaCurrent(
        IADLXDisplayGamma* gamma,
        const RecoveryEntry& snapshot)
    {
        if (gamma == nullptr)
            return false;

        adlx_bool current = false;
        ADLX_RESULT result = ADLX_FAIL;
        ADLX_GammaRamp ramp = {};
        ADLX_RegammaCoeff coefficient = {};
        switch (snapshot.gammaMode)
        {
        case GammaReRamp:
            return Succeeded(gamma->IsCurrentReGammaRamp(&current)) && current &&
                Succeeded(gamma->GetGammaRamp(&ramp)) &&
                std::memcmp(&ramp, &snapshot.ramp, sizeof(ramp)) == 0;
        case GammaDeRamp:
            return Succeeded(gamma->IsCurrentDeGammaRamp(&current)) && current &&
                Succeeded(gamma->GetGammaRamp(&ramp)) &&
                std::memcmp(&ramp, &snapshot.ramp, sizeof(ramp)) == 0;
        case GammaCoefficient:
            return Succeeded(gamma->IsCurrentRegammaCoefficient(&current)) && current &&
                Succeeded(gamma->GetGammaCoefficient(&coefficient)) &&
                std::memcmp(
                    &coefficient,
                    &snapshot.coefficient,
                    sizeof(coefficient)) == 0;
        case GammaSrgb:
            result = gamma->IsCurrentReGammaSRGB(&current);
            break;
        case GammaBt709:
            result = gamma->IsCurrentReGammaBT709(&current);
            break;
        case GammaPq:
            result = gamma->IsCurrentReGammaPQ(&current);
            break;
        case GammaPq2084Interim:
            result = gamma->IsCurrentReGammaPQ2084Interim(&current);
            break;
        case Gamma36:
            result = gamma->IsCurrentReGamma36(&current);
            break;
        default:
        {
            adlx_bool selected = false;
            if (!Succeeded(gamma->IsCurrentReGammaRamp(&selected)) || selected)
                return false;
            selected = false;
            if (!Succeeded(gamma->IsCurrentDeGammaRamp(&selected)) || selected)
                return false;
            selected = false;
            if (!Succeeded(gamma->IsCurrentRegammaCoefficient(&selected)) || selected)
                return false;
            selected = false;
            if (!Succeeded(gamma->IsCurrentReGammaSRGB(&selected)) || selected)
                return false;
            selected = false;
            if (!Succeeded(gamma->IsCurrentReGammaBT709(&selected)) || selected)
                return false;
            selected = false;
            if (!Succeeded(gamma->IsCurrentReGammaPQ(&selected)) || selected)
                return false;
            selected = false;
            if (!Succeeded(gamma->IsCurrentReGammaPQ2084Interim(&selected)) || selected)
                return false;
            selected = false;
            return Succeeded(gamma->IsCurrentReGamma36(&selected)) && !selected;
        }
        }
        return Succeeded(result) && current;
    }

    bool AreCustomValuesCurrent(
        IADLXDisplayCustomColor* color,
        const RecoveryEntry& snapshot)
    {
        if (color == nullptr)
            return snapshot.customFlags == 0;

        adlx_int value = 0;
        if ((snapshot.customFlags & HasBrightness) != 0 &&
            (!Succeeded(color->GetBrightness(&value)) || value != snapshot.brightness))
            return false;
        if ((snapshot.customFlags & HasContrast) != 0 &&
            (!Succeeded(color->GetContrast(&value)) || value != snapshot.contrast))
            return false;
        if ((snapshot.customFlags & HasSaturation) != 0 &&
            (!Succeeded(color->GetSaturation(&value)) || value != snapshot.saturation))
            return false;
        if ((snapshot.customFlags & HasHue) != 0 &&
            (!Succeeded(color->GetHue(&value)) || value != snapshot.hue))
            return false;
        if ((snapshot.customFlags & HasTemperature) != 0 &&
            (!Succeeded(color->GetTemperature(&value)) || value != snapshot.temperature))
            return false;
        return true;
    }

    bool SetGammaState(
        IADLXDisplayGamma* gamma,
        const RecoveryEntry& snapshot)
    {
        if (gamma == nullptr)
            return false;

        ADLX_RESULT result = ADLX_FAIL;
        switch (snapshot.gammaMode)
        {
        case GammaReRamp:
            result = gamma->SetReGammaRamp(snapshot.ramp);
            break;
        case GammaDeRamp:
            result = gamma->SetDeGammaRamp(snapshot.ramp);
            break;
        case GammaCoefficient:
            result = gamma->SetReGammaCoefficient(snapshot.coefficient);
            break;
        case GammaSrgb:
            result = gamma->SetReGammaSRGB();
            break;
        case GammaBt709:
            result = gamma->SetReGammaBT709();
            break;
        case GammaPq:
            result = gamma->SetReGammaPQ();
            break;
        case GammaPq2084Interim:
            result = gamma->SetReGammaPQ2084Interim();
            break;
        case Gamma36:
            result = gamma->SetReGamma36();
            break;
        default:
            result = gamma->ResetGammaRamp();
            break;
        }
        return Succeeded(result);
    }

    bool RestoreSnapshot(const DisplayEntry& entry, const RecoveryEntry& snapshot)
    {
        bool succeeded = true;
        IADLXDisplayCustomColorPtr color;
        if (snapshot.customFlags != 0)
        {
            if (!GetCustomColor(entry, color))
            {
                succeeded = false;
            }
            else
            {
                if ((snapshot.customFlags & HasBrightness) != 0)
                    succeeded &= Succeeded(color->SetBrightness(snapshot.brightness));
                if ((snapshot.customFlags & HasContrast) != 0)
                    succeeded &= Succeeded(color->SetContrast(snapshot.contrast));
                if ((snapshot.customFlags & HasSaturation) != 0)
                    succeeded &= Succeeded(color->SetSaturation(snapshot.saturation));
                if ((snapshot.customFlags & HasHue) != 0)
                    succeeded &= Succeeded(color->SetHue(snapshot.hue));
                if ((snapshot.customFlags & HasTemperature) != 0)
                    succeeded &= Succeeded(color->SetTemperature(snapshot.temperature));
            }
        }

        IADLXDisplayGammaPtr gamma;
        if (!GetGamma(entry, gamma))
        {
            succeeded = false;
        }
        else
        {
            succeeded &= SetGammaState(gamma, snapshot);
        }

        if (!succeeded)
            return false;

        return AreCustomValuesCurrent(color, snapshot) &&
            IsGammaCurrent(gamma, snapshot);
    }

    bool RecoverSnapshotConditionally(
        const DisplayEntry& entry,
        const RecoveryEntry& snapshot,
        bool& externalChangePreserved)
    {
        IADLXDisplayCustomColorPtr color;
        IADLXDisplayGammaPtr gamma;
        if (!GetCustomColor(entry, color) || !GetGamma(entry, gamma))
            return false;

        auto recoverValue = [&](auto getter, auto setter,
            int original, int applied, int previousApplied) -> bool
        {
            adlx_int current = 0;
            if (!Succeeded(getter(&current)))
                return false;
            if (current == original)
                return true;

            bool ownedValue = snapshot.appliedValid != 0 && current == applied;
            ownedValue |= snapshot.previousAppliedValid != 0 &&
                current == previousApplied;
            if (!ownedValue)
            {
                externalChangePreserved = true;
                return true;
            }

            if (!Succeeded(setter(original)) ||
                !Succeeded(getter(&current)) || current != original)
                return false;
            return true;
        };

        bool succeeded = recoverValue(
            [&](adlx_int* value) { return color->GetBrightness(value); },
            [&](adlx_int value) { return color->SetBrightness(value); },
            snapshot.brightness,
            snapshot.appliedBrightness,
            snapshot.previousAppliedBrightness);
        succeeded &= recoverValue(
            [&](adlx_int* value) { return color->GetContrast(value); },
            [&](adlx_int value) { return color->SetContrast(value); },
            snapshot.contrast,
            snapshot.appliedContrast,
            snapshot.previousAppliedContrast);
        succeeded &= recoverValue(
            [&](adlx_int* value) { return color->GetSaturation(value); },
            [&](adlx_int value) { return color->SetSaturation(value); },
            snapshot.saturation,
            snapshot.appliedSaturation,
            snapshot.previousAppliedSaturation);

        if (IsGammaCurrent(gamma, snapshot))
            return succeeded;

        RecoveryEntry applied = {};
        applied.gammaMode = GammaReRamp;
        bool ownedGamma = false;
        if (snapshot.appliedValid != 0)
        {
            applied.ramp = snapshot.appliedRamp;
            ownedGamma = IsGammaCurrent(gamma, applied);
        }
        if (!ownedGamma && snapshot.previousAppliedValid != 0)
        {
            applied.ramp = snapshot.previousAppliedRamp;
            ownedGamma = IsGammaCurrent(gamma, applied);
        }

        if (!ownedGamma)
        {
            externalChangePreserved = true;
            return succeeded;
        }
        if (!SetGammaState(gamma, snapshot) || !IsGammaCurrent(gamma, snapshot))
            return false;
        return succeeded;
    }

    std::vector<DisplayEntry*> GetSessionTargets()
    {
        std::vector<DisplayEntry*> targets;
        for (DisplayEntry& entry : g_displays)
            if (entry.snapshotValid)
                targets.push_back(&entry);
        return targets;
    }

    std::vector<DisplayEntry*> SelectTargets(
        int left,
        int top,
        int width,
        int height)
    {
        std::vector<DisplayEntry*> targets;
        IADLXSystem* system = g_ADLX.GetSystemServices();
        IADLXDesktopServicesPtr desktopServices;
        if (system != nullptr &&
            Succeeded(system->GetDesktopsServices(&desktopServices)) &&
            desktopServices != nullptr)
        {
            IADLXDesktopListPtr desktops;
            if (Succeeded(desktopServices->GetDesktops(&desktops)) && desktops != nullptr)
            {
                for (adlx_uint index = desktops->Begin(); index < desktops->Size(); ++index)
                {
                    IADLXDesktopPtr desktop;
                    if (!Succeeded(desktops->At(index, &desktop)) || desktop == nullptr)
                        continue;

                    ADLX_Point point = {};
                    adlx_int desktopWidth = 0;
                    adlx_int desktopHeight = 0;
                    if (!Succeeded(desktop->TopLeft(&point)) ||
                        !Succeeded(desktop->Size(&desktopWidth, &desktopHeight)) ||
                        point.x != left || point.y != top ||
                        desktopWidth != width || desktopHeight != height)
                        continue;

                    IADLXDisplayListPtr displays;
                    if (!Succeeded(desktop->GetDisplays(&displays)) || displays == nullptr)
                        continue;

                    for (adlx_uint displayIndex = displays->Begin();
                        displayIndex < displays->Size();
                        ++displayIndex)
                    {
                        IADLXDisplayPtr display;
                        if (!Succeeded(displays->At(displayIndex, &display)) || display == nullptr)
                            continue;
                        adlx_size uniqueId = 0;
                        if (!Succeeded(display->UniqueId(&uniqueId)))
                            continue;
                        auto match = std::find_if(
                            g_displays.begin(),
                            g_displays.end(),
                            [uniqueId](const DisplayEntry& entry)
                            {
                                return entry.uniqueId == static_cast<std::uint64_t>(uniqueId);
                            });
                        if (match != g_displays.end())
                            targets.push_back(&(*match));
                    }
                    if (!targets.empty())
                        return targets;
                }
            }
        }

        // Refuse to guess by resolution: two monitors may share a native mode,
        // and applying persistent driver color to the wrong output is unsafe.
        return targets;
    }

    bool WriteRecoveryFile()
    {
        if (g_recoveryPath.empty())
        {
            SetError(L"The AMD recovery-file path is empty.");
            return false;
        }

        std::filesystem::path path(g_recoveryPath);
        std::error_code error;
        std::filesystem::create_directories(path.parent_path(), error);
        std::filesystem::path temporary = path;
        temporary += L".tmp";

        std::ofstream stream(temporary, std::ios::binary | std::ios::trunc);
        if (!stream)
        {
            SetError(L"The AMD recovery file could not be created.");
            return false;
        }

        std::vector<DisplayEntry*> targets = GetSessionTargets();
        std::vector<RecoveryEntry> recoveryEntries;
        recoveryEntries.reserve(targets.size());
        for (const DisplayEntry* entry : targets)
            recoveryEntries.push_back(entry->snapshot);

        RecoveryHeader header = {
            RecoveryMagic,
            RecoveryVersion,
            static_cast<std::uint32_t>(targets.size()),
            Crc32(
                recoveryEntries.data(),
                recoveryEntries.size() * sizeof(RecoveryEntry))
        };
        stream.write(reinterpret_cast<const char*>(&header), sizeof(header));
        stream.write(
            reinterpret_cast<const char*>(recoveryEntries.data()),
            static_cast<std::streamsize>(
                recoveryEntries.size() * sizeof(RecoveryEntry)));
        stream.flush();
        bool good = stream.good();
        stream.close();
        if (!good || !MoveFileExW(
            temporary.c_str(),
            path.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        {
            DeleteFileW(temporary.c_str());
            SetError(L"The AMD recovery file could not be committed.");
            return false;
        }
        return true;
    }

    bool ReadRecoveryFile(std::vector<RecoveryEntry>& entries)
    {
        entries.clear();
        if (!FileExists(g_recoveryPath))
        {
            return false;
        }

        std::ifstream stream(std::filesystem::path(g_recoveryPath), std::ios::binary);
        RecoveryHeader header = {};
        stream.read(reinterpret_cast<char*>(&header), sizeof(header));
        if (!stream || header.magic != RecoveryMagic ||
            header.version != RecoveryVersion ||
            header.displayCount == 0 ||
            header.displayCount > MaxRecoveryDisplays)
        {
            SetError(L"The AMD recovery file is invalid.");
            return false;
        }

        entries.resize(header.displayCount);
        stream.read(
            reinterpret_cast<char*>(entries.data()),
            static_cast<std::streamsize>(entries.size() * sizeof(RecoveryEntry)));
        if (!stream)
        {
            entries.clear();
            SetError(L"The AMD recovery file is incomplete.");
            return false;
        }
        if (stream.peek() != std::char_traits<char>::eof() ||
            Crc32(entries.data(), entries.size() * sizeof(RecoveryEntry)) !=
                header.reserved)
        {
            entries.clear();
            SetError(L"The AMD recovery file failed its integrity check.");
            return false;
        }
        constexpr std::uint32_t requiredFlags =
            HasBrightness | HasContrast | HasSaturation;
        for (const RecoveryEntry& entry : entries)
        {
            if (entry.uniqueId == 0 || entry.customFlags != requiredFlags ||
                entry.gammaMode < GammaDefault || entry.gammaMode > Gamma36 ||
                entry.appliedValid > 1 || entry.previousAppliedValid > 1 ||
                (entry.previousAppliedValid != 0 && entry.appliedValid == 0))
            {
                entries.clear();
                SetError(L"The AMD recovery file contains an invalid display record.");
                return false;
            }
        }
        return true;
    }

    bool DeleteRecoveryFile()
    {
        if (g_recoveryPath.empty())
            return true;

        bool deleted = DeleteFileW(g_recoveryPath.c_str()) != FALSE ||
            GetLastError() == ERROR_FILE_NOT_FOUND;
        std::wstring temporary = g_recoveryPath + L".tmp";
        bool temporaryDeleted = DeleteFileW(temporary.c_str()) != FALSE ||
            GetLastError() == ERROR_FILE_NOT_FOUND;
        if (!deleted || !temporaryDeleted)
        {
            SetError(L"The restored AMD recovery journal could not be deleted safely.");
            return false;
        }
        return true;
    }

    bool RecoverPreviousSession()
    {
        std::vector<RecoveryEntry> recovered;
        if (!ReadRecoveryFile(recovered))
        {
            return !FileExists(g_recoveryPath);
        }

        bool allRestored = true;
        bool externalChangePreserved = false;
        for (const RecoveryEntry& snapshot : recovered)
        {
            auto match = std::find_if(
                g_displays.begin(),
                g_displays.end(),
                [&snapshot](const DisplayEntry& entry)
                {
                    return entry.uniqueId == snapshot.uniqueId;
                });
            if (match == g_displays.end() ||
                !RecoverSnapshotConditionally(
                    *match,
                    snapshot,
                    externalChangePreserved))
                allRestored = false;
        }

        if (allRestored)
            allRestored = DeleteRecoveryFile();
        if (allRestored && externalChangePreserved)
        {
            g_recoveryPreservedExternal = true;
            SetError(L"External AMD color changes were preserved during recovery.");
        }
        if (!allRestored && g_lastError.empty())
            SetError(L"Not every AMD display could be recovered from the previous session.");
        return allRestored;
    }

    bool BeginSession(const std::vector<DisplayEntry*>& targets)
    {
        if (g_sessionActive)
            return true;

        if (targets.empty())
        {
            SetError(L"AMD ADLX could not match the selected Windows display.");
            return false;
        }

        for (DisplayEntry& entry : g_displays)
            entry.snapshotValid = false;
        for (DisplayEntry* entry : targets)
        {
            if (entry == nullptr || !CaptureSnapshot(*entry))
            {
                for (DisplayEntry& cleanup : g_displays)
                    cleanup.snapshotValid = false;
                return false;
            }
        }

        if (!WriteRecoveryFile())
        {
            for (DisplayEntry& entry : g_displays)
                entry.snapshotValid = false;
            return false;
        }

        g_sessionActive = true;
        return true;
    }

    bool RestoreSession()
    {
        if (!g_sessionActive)
            return true;

        bool restored = true;
        bool externalChangePreserved = false;
        for (DisplayEntry& entry : g_displays)
        {
            if (entry.snapshotValid)
                restored &= RecoverSnapshotConditionally(
                    entry,
                    entry.snapshot,
                    externalChangePreserved);
        }

        if (restored)
        {
            restored = DeleteRecoveryFile();
            if (restored)
            {
                g_sessionActive = false;
                for (DisplayEntry& entry : g_displays)
                    entry.snapshotValid = false;
                if (externalChangePreserved)
                {
                    g_recoveryPreservedExternal = true;
                    SetError(L"External AMD color changes were preserved while disabling the filter.");
                }
            }
        }
        else
        {
            SetError(L"The original AMD display colors could not be fully restored.");
        }
        return restored;
    }

    int AdjustValue(int original, float delta, const ADLX_IntRange& range)
    {
        double halfRange =
            static_cast<double>(range.maxValue - range.minValue) / 2.0;
        double requested = static_cast<double>(original) +
            static_cast<double>(delta) * halfRange;
        int step = std::max(1, range.step);
        int stepped = range.minValue + static_cast<int>(std::llround(
            (requested - range.minValue) / step)) * step;
        return std::max(range.minValue, std::min(range.maxValue, stepped));
    }

    bool BuildGammaRamp(
        const RecoveryEntry& original,
        const AmdColorSettings& requested,
        ADLX_GammaRamp& ramp)
    {
        AmdColorSettings settings = requested;
        settings.gamma = std::clamp(settings.gamma, 0.50f, 2.50f);
        settings.shadowBoost = std::clamp(settings.shadowBoost, 0.0f, 0.50f);
        settings.redGain = std::clamp(settings.redGain, 0.50f, 1.50f);
        settings.greenGain = std::clamp(settings.greenGain, 0.50f, 1.50f);
        settings.blueGain = std::clamp(settings.blueGain, 0.50f, 1.50f);
        const double gains[3] = {
            settings.redGain,
            settings.greenGain,
            settings.blueGain
        };

        ramp = {};
        for (int channel = 0; channel < 3; ++channel)
        {
            for (int index = 0; index < 256; ++index)
            {
                double source = index / 255.0;
                double value = source;
                switch (original.gammaMode)
                {
                case GammaReRamp:
                    value = original.ramp.gamma[index + channel * 256] / 65535.0;
                    break;
                case GammaCoefficient:
                {
                    // AMD's documented default tuple is the standard sRGB
                    // transfer. ADLX does not expose an effective ramp while a
                    // coefficient mode is active, so unknown/custom tuples are
                    // deliberately left to the non-destructive shader fallback.
                    if (original.coefficient.coefficientA0 != 31308 ||
                        original.coefficient.coefficientA1 != 12920 ||
                        original.coefficient.coefficientA2 != 55 ||
                        original.coefficient.coefficientA3 != 55 ||
                        original.coefficient.gamma != 2400)
                    {
                        SetError(L"A custom AMD coefficient calibration is active; using shader fallback.");
                        return false;
                    }
                    double threshold = original.coefficient.coefficientA0 / 10000000.0;
                    double slope = original.coefficient.coefficientA1 / 1000.0;
                    double add = original.coefficient.coefficientA2 / 1000.0;
                    double subtract = original.coefficient.coefficientA3 / 1000.0;
                    double baseGamma = original.coefficient.gamma / 1000.0;
                    if (threshold < 0.0 || slope <= 0.0 || baseGamma <= 0.0)
                    {
                        SetError(L"AMD returned invalid baseline gamma coefficients.");
                        return false;
                    }
                    value = source < threshold
                        ? source * slope
                        : (1.0 + add) * std::pow(source, 1.0 / baseGamma) - subtract;
                    break;
                }
                case GammaSrgb:
                    value = source < 0.0031308
                        ? source * 12.92
                        : 1.055 * std::pow(source, 1.0 / 2.4) - 0.055;
                    break;
                case GammaBt709:
                    value = source < 0.018
                        ? source * 4.5
                        : 1.099 * std::pow(source, 0.45) - 0.099;
                    break;
                case Gamma36:
                    value = std::pow(source, 1.0 / 3.6);
                    break;
                default:
                    SetError(L"The current AMD gamma mode cannot be safely composed; using shader fallback.");
                    return false;
                }
                value = std::clamp(value, 0.0, 1.0);
                double shadowMask = 1.0 - value;
                shadowMask *= shadowMask;
                value = std::clamp(
                    value + settings.shadowBoost * shadowMask,
                    0.0,
                    1.0);
                value = std::pow(value, 1.0 / settings.gamma);
                value = std::clamp(value * gains[channel], 0.0, 1.0);
                ramp.gamma[index + channel * 256] =
                    static_cast<adlx_uint16>(std::llround(value * 65535.0));
            }
        }
        return true;
    }

    bool PrepareAppliedProfile(
        DisplayEntry& entry,
        const AmdColorSettings& settings)
    {
        if (!entry.snapshotValid)
            return false;

        IADLXDisplayCustomColorPtr color;
        if (!GetCustomColor(entry, color))
            return false;

        ADLX_IntRange range = {};
        if (!Succeeded(color->GetBrightnessRange(&range)))
            return false;
        int brightness = AdjustValue(
            entry.snapshot.brightness,
            std::clamp(settings.brightness, -0.20f, 0.30f),
            range);

        if (!Succeeded(color->GetContrastRange(&range)))
            return false;
        int contrast = AdjustValue(
            entry.snapshot.contrast,
            std::clamp(settings.contrast, 0.50f, 1.50f) - 1.0f,
            range);

        if (!Succeeded(color->GetSaturationRange(&range)))
            return false;
        int saturation = AdjustValue(
            entry.snapshot.saturation,
            std::clamp(settings.saturation, 0.0f, 2.0f) - 1.0f,
            range);

        ADLX_GammaRamp ramp = {};
        if (!BuildGammaRamp(entry.snapshot, settings, ramp))
            return false;

        entry.snapshot.previousAppliedValid = entry.snapshot.appliedValid;
        entry.snapshot.previousAppliedBrightness = entry.snapshot.appliedBrightness;
        entry.snapshot.previousAppliedContrast = entry.snapshot.appliedContrast;
        entry.snapshot.previousAppliedSaturation = entry.snapshot.appliedSaturation;
        entry.snapshot.previousAppliedRamp = entry.snapshot.appliedRamp;
        entry.snapshot.appliedValid = 1;
        entry.snapshot.appliedBrightness = brightness;
        entry.snapshot.appliedContrast = contrast;
        entry.snapshot.appliedSaturation = saturation;
        entry.snapshot.appliedRamp = ramp;
        return true;
    }

    bool ApplyToDisplay(DisplayEntry& entry)
    {
        if (!entry.snapshotValid || !entry.gamma || !entry.customColor ||
            !entry.brightness || !entry.contrast || !entry.saturation ||
            entry.snapshot.appliedValid == 0)
            return false;

        IADLXDisplayGammaPtr gamma;
        if (!GetGamma(entry, gamma))
            return false;
        const ADLX_GammaRamp& ramp = entry.snapshot.appliedRamp;
        ADLX_RESULT result = gamma->SetReGammaRamp(ramp);
        if (!Succeeded(result))
        {
            SetAdlxError(L"AMD SetReGammaRamp", result);
            return false;
        }

        IADLXDisplayCustomColorPtr color;
        if (!GetCustomColor(entry, color))
        {
            RestoreSnapshot(entry, entry.snapshot);
            return false;
        }

        int brightness = entry.snapshot.appliedBrightness;
        int contrast = entry.snapshot.appliedContrast;
        int saturation = entry.snapshot.appliedSaturation;

        if (!Succeeded(color->SetBrightness(brightness)) ||
            !Succeeded(color->SetContrast(contrast)) ||
            !Succeeded(color->SetSaturation(saturation)))
        {
            RestoreSnapshot(entry, entry.snapshot);
            SetError(L"AMD custom color rejected one of the requested values.");
            return false;
        }

        RecoveryEntry applied = {};
        applied.customFlags = HasBrightness | HasContrast | HasSaturation;
        applied.brightness = brightness;
        applied.contrast = contrast;
        applied.saturation = saturation;
        applied.gammaMode = GammaReRamp;
        applied.ramp = ramp;
        if (!AreCustomValuesCurrent(color, applied) ||
            !IsGammaCurrent(gamma, applied))
        {
            RestoreSnapshot(entry, entry.snapshot);
            SetError(L"AMD did not read back the complete requested color profile.");
            return false;
        }

        return true;
    }
}

static int InitializeCore(const wchar_t* recoveryPath)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_initialized)
        return 0;

    g_lastError.clear();
    g_recoveryBlocked = false;
    g_recoveryPreservedExternal = false;
    g_recoveryPath = recoveryPath == nullptr ? L"" : recoveryPath;
    ADLX_RESULT result = g_ADLX.Initialize();
    if (!Succeeded(result))
    {
        SetAdlxError(L"AMD ADLX initialization", result);
        return 10;
    }
    g_adlxStarted = true;

    IADLXSystem* system = g_ADLX.GetSystemServices();
    if (system == nullptr)
    {
        SetError(L"AMD ADLX returned no system service.");
        g_ADLX.Terminate();
        g_adlxStarted = false;
        return 11;
    }

    result = system->GetDisplaysServices(&g_displayServices);
    if (!Succeeded(result) || g_displayServices == nullptr)
    {
        SetAdlxError(L"AMD GetDisplaysServices", result);
        g_displayServices = nullptr;
        g_ADLX.Terminate();
        g_adlxStarted = false;
        return 12;
    }

    IADLXDisplayListPtr displayList;
    result = g_displayServices->GetDisplays(&displayList);
    if (!Succeeded(result) || displayList == nullptr)
    {
        SetAdlxError(L"AMD GetDisplays", result);
        g_displayServices = nullptr;
        g_ADLX.Terminate();
        g_adlxStarted = false;
        return 13;
    }

    g_displays.clear();
    for (adlx_uint index = displayList->Begin(); index < displayList->Size(); ++index)
    {
        IADLXDisplayPtr display;
        if (!Succeeded(displayList->At(index, &display)) || display == nullptr)
            continue;

        DisplayEntry entry;
        entry.display = display;
        adlx_size uniqueId = 0;
        if (!Succeeded(display->UniqueId(&uniqueId)) || uniqueId == 0)
            continue;
        entry.uniqueId = static_cast<std::uint64_t>(uniqueId);

        IADLXDisplayGammaPtr gamma;
        entry.gamma = GetGamma(entry, gamma);

        IADLXDisplayCustomColorPtr color;
        entry.customColor = GetCustomColor(entry, color);
        if (entry.customColor)
        {
            adlx_bool supported = false;
            entry.brightness = Succeeded(color->IsBrightnessSupported(&supported)) && supported;
            supported = false;
            entry.contrast = Succeeded(color->IsContrastSupported(&supported)) && supported;
            supported = false;
            entry.saturation = Succeeded(color->IsSaturationSupported(&supported)) && supported;
        }
        g_displays.push_back(entry);
    }

    g_initialized = true;
    if (!g_recoveryPath.empty() && !RecoverPreviousSession())
        g_recoveryBlocked = true;

    if (g_displays.empty())
    {
        SetError(L"AMD ADLX found no connected displays.");
        displayList = nullptr;
        g_displayServices = nullptr;
        g_ADLX.Terminate();
        g_adlxStarted = false;
        g_initialized = false;
        return 14;
    }
    return 0;
}

static int GetCapabilitiesCore(AmdColorCapabilities* capabilities)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_initialized || capabilities == nullptr)
        return 20;

    *capabilities = {};
    capabilities->reserved = g_recoveryBlocked
        ? 1
        : (g_recoveryPreservedExternal ? 2 : 0);
    capabilities->displayCount = static_cast<int>(g_displays.size());
    for (const DisplayEntry& entry : g_displays)
    {
        if (entry.gamma) ++capabilities->gammaDisplayCount;
        if (entry.customColor) ++capabilities->customColorDisplayCount;
        if (entry.brightness) ++capabilities->brightnessDisplayCount;
        if (entry.contrast) ++capabilities->contrastDisplayCount;
        if (entry.saturation) ++capabilities->saturationDisplayCount;
        if (!g_recoveryBlocked && entry.gamma && entry.customColor && entry.brightness &&
            entry.contrast && entry.saturation)
            ++capabilities->fullFilterDisplayCount;
    }
    return 0;
}

static int ApplyCore(
    const AmdColorSettings* settings,
    int left,
    int top,
    int width,
    int height)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_initialized || settings == nullptr)
        return 30;
    if (g_recoveryBlocked)
    {
        SetError(L"AMD color recovery is still pending; a new driver filter was not applied.");
        return 33;
    }

    g_lastError.clear();
    std::vector<DisplayEntry*> targets = g_sessionActive
        ? GetSessionTargets()
        : SelectTargets(left, top, width, height);
    if (!BeginSession(targets))
        return 31;

    bool prepared = true;
    for (DisplayEntry* entry : targets)
    {
        if (entry == nullptr || !PrepareAppliedProfile(*entry, *settings))
            prepared = false;
    }
    if (!prepared || !WriteRecoveryFile())
    {
        RestoreSession();
        if (g_lastError.empty())
            SetError(L"AMD could not safely journal the requested color profile.");
        return 31;
    }

    int applied = 0;
    for (DisplayEntry* entry : targets)
    {
        if (entry != nullptr && ApplyToDisplay(*entry))
            ++applied;
    }

    if (applied != static_cast<int>(targets.size()))
    {
        RestoreSession();
        if (g_lastError.empty())
            SetError(L"Not every AMD display accepted the complete direct color profile.");
        return 32;
    }
    return 0;
}

static int RestoreCore()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_initialized)
        return 0;
    return RestoreSession() ? 0 : 40;
}

static int GetLastErrorCore(wchar_t* buffer, int characterCount)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (buffer == nullptr || characterCount <= 0)
        return 50;
    wcsncpy_s(buffer, static_cast<size_t>(characterCount), g_lastError.c_str(), _TRUNCATE);
    return 0;
}

static int ShutdownCore()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_adlxStarted)
        return 0;

    bool restored = !g_initialized || RestoreSession();
    g_displays.clear();
    g_displayServices = nullptr;
    ADLX_RESULT result = g_ADLX.Terminate();
    g_initialized = false;
    g_adlxStarted = false;
    g_sessionActive = false;
    g_recoveryBlocked = false;
    g_recoveryPreservedExternal = false;
    return restored && Succeeded(result) ? 0 : 60;
}

BRIDGE_EXPORT int __cdecl AmdColor_Initialize(const wchar_t* recoveryPath) noexcept
{
    try
    {
        return InitializeCore(recoveryPath);
    }
    catch (...)
    {
        try
        {
            std::lock_guard<std::mutex> lock(g_mutex);
            g_displays.clear();
            g_displayServices = nullptr;
            if (g_adlxStarted)
                g_ADLX.Terminate();
            g_initialized = false;
            g_adlxStarted = false;
            g_sessionActive = false;
            g_recoveryBlocked = false;
            g_recoveryPreservedExternal = false;
            SetError(L"Unexpected failure while initializing the AMD ADLX bridge.");
        }
        catch (...) {}
        return 15;
    }
}

BRIDGE_EXPORT int __cdecl AmdColor_GetCapabilities(
    AmdColorCapabilities* capabilities) noexcept
{
    try
    {
        return GetCapabilitiesCore(capabilities);
    }
    catch (...)
    {
        try { SetError(L"Unexpected failure while reading AMD capabilities."); }
        catch (...) {}
        return 21;
    }
}

BRIDGE_EXPORT int __cdecl AmdColor_Apply(
    const AmdColorSettings* settings,
    int left,
    int top,
    int width,
    int height) noexcept
{
    try
    {
        return ApplyCore(settings, left, top, width, height);
    }
    catch (...)
    {
        try { RestoreCore(); }
        catch (...) {}
        try { SetError(L"Unexpected AMD apply failure; original colors were requested."); }
        catch (...) {}
        return 34;
    }
}

BRIDGE_EXPORT int __cdecl AmdColor_Restore() noexcept
{
    try
    {
        return RestoreCore();
    }
    catch (...)
    {
        try { SetError(L"Unexpected failure while restoring AMD display colors."); }
        catch (...) {}
        return 41;
    }
}

BRIDGE_EXPORT int __cdecl AmdColor_GetLastError(
    wchar_t* buffer,
    int characterCount) noexcept
{
    try
    {
        return GetLastErrorCore(buffer, characterCount);
    }
    catch (...)
    {
        return 51;
    }
}

BRIDGE_EXPORT int __cdecl AmdColor_Shutdown() noexcept
{
    try
    {
        return ShutdownCore();
    }
    catch (...)
    {
        try
        {
            std::lock_guard<std::mutex> lock(g_mutex);
            g_displays.clear();
            g_displayServices = nullptr;
            if (g_adlxStarted)
                g_ADLX.Terminate();
            g_initialized = false;
            g_adlxStarted = false;
            g_sessionActive = false;
            g_recoveryBlocked = false;
            g_recoveryPreservedExternal = false;
        }
        catch (...) {}
        return 61;
    }
}
