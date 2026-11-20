// Shared WASAPI RAW capture. Only aggregate power, peak and counters leave this process.
#define NOMINMAX
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <endpointvolume.h>
#include <functiondiscoverykeys_devpkey.h>
#include <ks.h>
#include <ksmedia.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <memory>
#include <stdexcept>
#include <string>

using Microsoft::WRL::ComPtr;

static void check(HRESULT hr, const char* step) {
    if (FAILED(hr)) {
        char msg[160];
        std::snprintf(msg, sizeof msg, "%s (0x%08lx)", step, static_cast<unsigned long>(hr));
        throw std::runtime_error(msg);
    }
}

static ComPtr<IMMDevice> microphone() {
    ComPtr<IMMDeviceEnumerator> enumerator;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                           IID_PPV_ARGS(&enumerator)), "enumerate audio devices");
    ComPtr<IMMDeviceCollection> list;
    check(enumerator->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, &list), "capture endpoints");
    UINT count = 0;
    check(list->GetCount(&count), "endpoint count");
    for (UINT i = 0; i < count; ++i) {
        ComPtr<IMMDevice> dev;
        ComPtr<IPropertyStore> props;
        check(list->Item(i, &dev), "endpoint");
        check(dev->OpenPropertyStore(STGM_READ, &props), "endpoint properties");
        PROPVARIANT v{};
        const HRESULT hr = props->GetValue(PKEY_Device_FriendlyName, &v);
        const bool match = SUCCEEDED(hr) && v.vt == VT_LPWSTR && wcsstr(v.pwszVal, L"StreamCam");
        PropVariantClear(&v);
        if (match) return dev;
    }
    throw std::runtime_error("StreamCam capture endpoint unavailable");
}

struct Stats {
    unsigned long long samples = 0, nonzero = 0, clipped = 0;
    unsigned gaps = 0;
    double energy = 0, peak = 0;
    void add(double sample) {
        const double a = std::abs(sample);
        ++samples;
        nonzero += a > 0;
        clipped += a >= 0.99997;
        energy += sample * sample;
        peak = std::max(peak, a);
    }
};

static int capture() {
    auto dev = microphone();
    ComPtr<IAudioClient2> client;
    check(dev->Activate(__uuidof(IAudioClient2), CLSCTX_ALL, nullptr,
                        reinterpret_cast<void**>(client.GetAddressOf())), "audio client");
    AudioClientProperties props{};
    props.cbSize = sizeof props;
    props.eCategory = AudioCategory_Other;
    props.Options = AUDCLNT_STREAMOPTIONS_RAW;
    check(client->SetClientProperties(&props), "RAW mode");
    WAVEFORMATEX* raw = nullptr;
    check(client->GetMixFormat(&raw), "mix format");
    std::unique_ptr<WAVEFORMATEX, decltype(&CoTaskMemFree)> fmt(raw, CoTaskMemFree);
    bool floating = fmt->wFormatTag == WAVE_FORMAT_IEEE_FLOAT;
    bool pcm = fmt->wFormatTag == WAVE_FORMAT_PCM;
    if (fmt->wFormatTag == WAVE_FORMAT_EXTENSIBLE && fmt->cbSize >= 22) {
        const auto sub = reinterpret_cast<WAVEFORMATEXTENSIBLE*>(raw)->SubFormat;
        floating = sub == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        pcm = sub == KSDATAFORMAT_SUBTYPE_PCM;
    }
    const int bits = fmt->wBitsPerSample;
    if (!(floating && bits == 32) && !(pcm && (bits == 16 || bits == 24 || bits == 32)))
        throw std::runtime_error("unsupported native sample format");
    check(client->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 2000000, 0, raw, nullptr), "start RAW shared mode");
    ComPtr<IAudioCaptureClient> input;
    check(client->GetService(IID_PPV_ARGS(&input)), "capture service");
    float gain = -1; BOOL mute = FALSE;
    ComPtr<IAudioEndpointVolume> volume;
    if (SUCCEEDED(dev->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_ALL, nullptr,
                                reinterpret_cast<void**>(volume.GetAddressOf())))) {
        volume->GetMasterVolumeLevelScalar(&gain);
        volume->GetMute(&mute);
    }
    check(client->Start(), "start stream");
    std::printf("format %lu %u %d %s %.4f %d\n", fmt->nSamplesPerSec, fmt->nChannels, bits,
                floating ? "float" : "pcm", double(gain), int(mute));
    std::fflush(stdout);
    Stats stats;
    ULONGLONG last = GetTickCount64();
    for (;;) {
        UINT32 frames = 0;
        check(input->GetNextPacketSize(&frames), "next packet");
        if (!frames) {
            if (GetTickCount64() - last > 3000) throw std::runtime_error("no RAW packets");
            Sleep(5); continue;
        }
        BYTE* data = nullptr; DWORD flags = 0;
        check(input->GetBuffer(&data, &frames, &flags, nullptr, nullptr), "read packet");
        const size_t count = size_t(frames) * fmt->nChannels;
        if (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) ++stats.gaps;
        if (flags & AUDCLNT_BUFFERFLAGS_SILENT) stats.samples += count;
        else {
            for (size_t i = 0; i < count; ++i) {
                const BYTE* p = data + i * (bits / 8);
                if (floating) { float x; std::memcpy(&x,p,4); stats.add(x); }
                else if (bits == 16) { short x; std::memcpy(&x,p,2); stats.add(x / 32768.0); }
                else if (bits == 32) { int x; std::memcpy(&x,p,4); stats.add(x / 2147483648.0); }
                else {
                    int x = int(p[0]) | (int(p[1]) << 8) | (int(p[2]) << 16);
                    if (x & 0x800000) x -= 0x1000000;
                    stats.add(x / 8388608.0);
                }
            }
        }
        check(input->ReleaseBuffer(frames), "release packet");
        last = GetTickCount64();
        if (stats.samples >= fmt->nSamplesPerSec * size_t(fmt->nChannels) / 10) {
            if (std::printf("stats %llu %.17g %.9g %llu %llu %u\n", stats.samples, stats.energy,
                            stats.peak, stats.nonzero, stats.clipped, stats.gaps) < 0 ||
                std::fflush(stdout) != 0) return 0;
            stats = {};
        }
    }
}

int main() {
    try {
        check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "COM initialization");
        return capture();
    } catch (const std::exception& e) {
        std::printf("error %s\n",e.what());
        return 1;
    }
}
