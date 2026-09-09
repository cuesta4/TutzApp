#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <GameInput.h>
#include <stdio.h>
#include <stdint.h>

using namespace GameInput::v3;

static FILE* g_log = nullptr;

static void Log(const wchar_t* source, const wchar_t* format, ...)
{
    SYSTEMTIME st;
    GetLocalTime(&st);

    wchar_t message[1024];
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(message, _countof(message), _TRUNCATE, format, args);
    va_end(args);

    wchar_t line[1400];
    _snwprintf_s(
        line,
        _countof(line),
        _TRUNCATE,
        L"[%04u-%02u-%02u %02u:%02u:%02u.%03u] [%s] %s\n",
        st.wYear,
        st.wMonth,
        st.wDay,
        st.wHour,
        st.wMinute,
        st.wSecond,
        st.wMilliseconds,
        source,
        message);

    fputws(line, stdout);
    fflush(stdout);

    if (g_log)
    {
        fputws(line, g_log);
        fflush(g_log);
    }
}

static int ParseSeconds(int argc, wchar_t** argv)
{
    for (int i = 1; i + 1 < argc; ++i)
    {
        if (_wcsicmp(argv[i], L"--seconds") == 0 || _wcsicmp(argv[i], L"-s") == 0)
        {
            int seconds = _wtoi(argv[i + 1]);
            if (seconds < 5) return 5;
            if (seconds > 600) return 600;
            return seconds;
        }
    }

    return 90;
}

static const wchar_t* ParseLogPath(int argc, wchar_t** argv)
{
    for (int i = 1; i < argc; ++i)
    {
        size_t len = wcslen(argv[i]);
        if (len > 4 && _wcsicmp(argv[i] + len - 4, L".log") == 0)
        {
            return argv[i];
        }
    }

    return L"GameInputProbe.log";
}

static void AppendButton(wchar_t* buffer, size_t bufferCount, GameInputGamepadButtons buttons, GameInputGamepadButtons mask, const wchar_t* name)
{
    if ((buttons & mask) == 0)
    {
        return;
    }

    if (buffer[0] != 0)
    {
        wcscat_s(buffer, bufferCount, L"+");
    }

    wcscat_s(buffer, bufferCount, name);
}

static void FormatButtons(GameInputGamepadButtons buttons, wchar_t* buffer, size_t bufferCount)
{
    buffer[0] = 0;
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadMenu, L"START/MENU");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadView, L"BACK/VIEW");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadA, L"A");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadB, L"B");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadX, L"X");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadY, L"Y");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadDPadUp, L"UP");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadDPadDown, L"DOWN");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadDPadLeft, L"LEFT");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadDPadRight, L"RIGHT");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadLeftShoulder, L"LB");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadRightShoulder, L"RB");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadLeftThumbstick, L"L-THUMB");
    AppendButton(buffer, bufferCount, buttons, GameInputGamepadRightThumbstick, L"R-THUMB");

    if (buffer[0] == 0)
    {
        wcscpy_s(buffer, bufferCount, L"none");
    }
}

int wmain(int argc, wchar_t** argv)
{
    int seconds = ParseSeconds(argc, argv);
    const wchar_t* logPath = ParseLogPath(argc, argv);

    _wfopen_s(&g_log, logPath, L"w, ccs=UTF-8");
    Log(L"START", L"GameInput probe iniciado. duration=%ds log=%s", seconds, logPath);

    IGameInput* gameInput = nullptr;
    HRESULT hr = GameInputCreate(&gameInput);
    if (FAILED(hr) || !gameInput)
    {
        Log(L"GAMEINPUT", L"GameInputCreate falhou. hr=0x%08X", static_cast<unsigned>(hr));
        if (g_log) fclose(g_log);
        return 1;
    }

    gameInput->SetFocusPolicy(static_cast<GameInputFocusPolicy>(
        GameInputEnableBackgroundInput |
        GameInputEnableBackgroundGuideButton |
        GameInputEnableBackgroundShareButton));
    Log(L"GAMEINPUT", L"Inicializado com background input habilitado.");

    uint64_t deadline = GetTickCount64() + static_cast<uint64_t>(seconds) * 1000;
    IGameInputDevice* device = nullptr;
    GameInputGamepadButtons lastButtons = static_cast<GameInputGamepadButtons>(0xFFFFFFFF);
    HRESULT lastFailure = S_OK;
    bool hadReading = false;

    while (GetTickCount64() < deadline)
    {
        IGameInputReading* reading = nullptr;
        hr = gameInput->GetCurrentReading(GameInputKindGamepad, device, &reading);
        if (SUCCEEDED(hr) && reading)
        {
            if (!hadReading)
            {
                Log(L"GAMEINPUT", L"connected=True");
                hadReading = true;
            }

            if (!device)
            {
                reading->GetDevice(&device);
            }

            GameInputGamepadState state = {};
            if (reading->GetGamepadState(&state) && state.buttons != lastButtons)
            {
                wchar_t names[512];
                FormatButtons(state.buttons, names, _countof(names));
                bool backX = (state.buttons & (GameInputGamepadView | GameInputGamepadX)) == (GameInputGamepadView | GameInputGamepadX);
                Log(
                    L"GAMEINPUT",
                    L"buttons=0x%08X [%s]%s",
                    static_cast<unsigned>(state.buttons),
                    names,
                    backX ? L" BACK+X_DETECTED" : L"");
                lastButtons = state.buttons;
            }

            reading->Release();
        }
        else
        {
            if (hr != lastFailure)
            {
                Log(L"GAMEINPUT", L"reading unavailable. hr=0x%08X", static_cast<unsigned>(hr));
                lastFailure = hr;
            }

            if (device)
            {
                device->Release();
                device = nullptr;
            }

            if (hadReading)
            {
                Log(L"GAMEINPUT", L"connected=False");
                hadReading = false;
            }

            lastButtons = static_cast<GameInputGamepadButtons>(0xFFFFFFFF);
        }

        Sleep(16);
    }

    if (device) device->Release();
    gameInput->Release();
    Log(L"END", L"GameInput probe finalizado.");
    if (g_log) fclose(g_log);
    return 0;
}
