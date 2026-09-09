#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define STRSAFE_NO_DEPRECATE
#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <strsafe.h>

#include <array>
#include <cstring>
#include <cwchar>
#include <new>
#include <string>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")

namespace
{
    // These IDs must match build/ContextMenu/AppxManifest.xml and
    // ModernContextMenuWarmup.cs. They intentionally differ from the pre-v6
    // registration so Explorer cannot reuse a stale COM surrogate instance.
    constexpr CLSID CLSID_TutzTerminalExplorerCommand =
        {0xf13bf4b5, 0x9064, 0x4971, {0xb7, 0x3e, 0x1d, 0x5e, 0xc8, 0xf2, 0xea, 0xa5}};

    constexpr GUID GUID_TutzTerminalNormal =
        {0xa2731564, 0xc3ce, 0x4de1, {0x8f, 0xee, 0x78, 0x18, 0xb7, 0x54, 0x00, 0xdf}};

    constexpr GUID GUID_TutzTerminalElevated =
        {0x96992399, 0x7aa8, 0x41b6, {0xbf, 0xe1, 0xc2, 0x58, 0x24, 0x0d, 0xdc, 0xdc}};

    volatile LONG g_objectCount = 0;
    HMODULE g_module = nullptr;

    enum class CommandKind
    {
        Parent,
        Normal,
        Elevated
    };

    constexpr wchar_t ContextMenuRegistryPath[] = L"Software\\TutzApp\\ShellIntegration";
    constexpr wchar_t ShowNormalValueName[] = L"ShowNormal";
    constexpr wchar_t ShowElevatedValueName[] = L"ShowElevated";

    const wchar_t* CommandKindName(CommandKind kind) noexcept
    {
        switch (kind)
        {
            case CommandKind::Parent:
                return L"Parent";
            case CommandKind::Normal:
                return L"Normal";
            case CommandKind::Elevated:
                return L"Elevated";
        }
        return L"Unknown";
    }

    void TraceNativeEvent(
        const wchar_t* eventName,
        CommandKind kind = CommandKind::Parent,
        HRESULT result = S_OK) noexcept
    {
        if (!eventName)
        {
            return;
        }

        wchar_t localAppData[MAX_PATH]{};
        if (FAILED(SHGetFolderPathW(
                nullptr,
                CSIDL_LOCAL_APPDATA,
                nullptr,
                SHGFP_TYPE_CURRENT,
                localAppData)))
        {
            return;
        }

        wchar_t logDirectory[MAX_PATH]{};
        if (FAILED(StringCchCopyW(logDirectory, ARRAYSIZE(logDirectory), localAppData)) ||
            !PathAppendW(logDirectory, L"TutzApp"))
        {
            return;
        }
        CreateDirectoryW(logDirectory, nullptr);
        if (!PathAppendW(logDirectory, L"ShellIntegration"))
        {
            return;
        }
        CreateDirectoryW(logDirectory, nullptr);

        wchar_t logPath[MAX_PATH]{};
        if (FAILED(StringCchCopyW(logPath, ARRAYSIZE(logPath), logDirectory)) ||
            !PathAppendW(logPath, L"modern-context-menu-native.log"))
        {
            return;
        }

        SYSTEMTIME now{};
        GetLocalTime(&now);

        APTTYPE apartmentType = APTTYPE_CURRENT;
        APTTYPEQUALIFIER apartmentQualifier = APTTYPEQUALIFIER_NONE;
        const HRESULT apartmentResult = CoGetApartmentType(&apartmentType, &apartmentQualifier);

        wchar_t wideLine[768]{};
        if (FAILED(StringCchPrintfW(
                wideLine,
                ARRAYSIZE(wideLine),
                L"[%04u-%02u-%02u %02u:%02u:%02u.%03u] pid=%lu tid=%lu apt=%ld/%ld aptHr=0x%08lX event=%s kind=%s hr=0x%08lX\r\n",
                now.wYear,
                now.wMonth,
                now.wDay,
                now.wHour,
                now.wMinute,
                now.wSecond,
                now.wMilliseconds,
                GetCurrentProcessId(),
                GetCurrentThreadId(),
                static_cast<LONG>(apartmentType),
                static_cast<LONG>(apartmentQualifier),
                static_cast<ULONG>(apartmentResult),
                eventName,
                CommandKindName(kind),
                static_cast<ULONG>(result))))
        {
            return;
        }

        char utf8Line[2304]{};
        const int byteCount = WideCharToMultiByte(
            CP_UTF8,
            0,
            wideLine,
            -1,
            utf8Line,
            static_cast<int>(ARRAYSIZE(utf8Line)),
            nullptr,
            nullptr);
        if (byteCount <= 1)
        {
            return;
        }

        HANDLE file = CreateFileW(
            logPath,
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return;
        }

        DWORD written = 0;
        WriteFile(file, utf8Line, static_cast<DWORD>(byteCount - 1), &written, nullptr);
        CloseHandle(file);
    }

    bool ReadCommandEnabled(const wchar_t* valueName) noexcept
    {
        DWORD value = 1;
        DWORD valueSize = sizeof(value);
        DWORD valueType = 0;
        const LONG result = RegGetValueW(
            HKEY_CURRENT_USER,
            ContextMenuRegistryPath,
            valueName,
            RRF_RT_REG_DWORD,
            &valueType,
            &value,
            &valueSize);

        if (result == ERROR_FILE_NOT_FOUND || result == ERROR_PATH_NOT_FOUND)
        {
            return true;
        }

        return result == ERROR_SUCCESS ? value != 0 : true;
    }

    bool IsCommandEnabled(CommandKind kind) noexcept
    {
        switch (kind)
        {
            case CommandKind::Normal:
                return ReadCommandEnabled(ShowNormalValueName);
            case CommandKind::Elevated:
                return ReadCommandEnabled(ShowElevatedValueName);
            case CommandKind::Parent:
                return ReadCommandEnabled(ShowNormalValueName) ||
                    ReadCommandEnabled(ShowElevatedValueName);
        }

        return true;
    }

    HRESULT DuplicateString(const wchar_t* value, PWSTR* result) noexcept
    {
        if (!result)
        {
            return E_POINTER;
        }

        *result = nullptr;
        if (!value)
        {
            return E_INVALIDARG;
        }

        return SHStrDupW(value, result);
    }

    HRESULT GetParentIconReference(PWSTR* icon) noexcept
    {
        if (!icon)
        {
            return E_POINTER;
        }
        *icon = nullptr;

        wchar_t modulePath[MAX_PATH]{};
        const DWORD length = g_module
            ? GetModuleFileNameW(g_module, modulePath, ARRAYSIZE(modulePath))
            : 0;
        if (length == 0 || length >= ARRAYSIZE(modulePath))
        {
            return E_NOTIMPL;
        }

        // The DLL lives in <root>\ShellIntegration. Derive the application path
        // without probing the filesystem so GetIcon remains deterministic and cheap.
        wchar_t* separator = wcsrchr(modulePath, L'\\');
        if (!separator)
        {
            return E_NOTIMPL;
        }
        *separator = L'\0';
        separator = wcsrchr(modulePath, L'\\');
        if (!separator)
        {
            return E_NOTIMPL;
        }
        *separator = L'\0';

        if (FAILED(StringCchCatW(modulePath, ARRAYSIZE(modulePath), L"\\TutzApp.exe,0")))
        {
            return E_NOTIMPL;
        }

        return DuplicateString(modulePath, icon);
    }

    std::wstring ExpandEnvironmentString(const wchar_t* value)
    {
        const DWORD required = ExpandEnvironmentStringsW(value, nullptr, 0);
        if (required == 0)
        {
            return {};
        }

        std::wstring expanded(required, L'\0');
        const DWORD written = ExpandEnvironmentStringsW(value, expanded.data(), required);
        if (written == 0 || written > required)
        {
            return {};
        }

        expanded.resize(written - 1);
        return expanded;
    }

    std::wstring ResolveWindowsTerminalPath()
    {
        wchar_t resolved[MAX_PATH]{};
        const DWORD found = SearchPathW(nullptr, L"wt.exe", nullptr, ARRAYSIZE(resolved), resolved, nullptr);
        if (found > 0 && found < ARRAYSIZE(resolved))
        {
            return resolved;
        }

        std::wstring aliasPath = ExpandEnvironmentString(L"%LOCALAPPDATA%\\Microsoft\\WindowsApps\\wt.exe");
        if (!aliasPath.empty() && GetFileAttributesW(aliasPath.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            return aliasPath;
        }

        return L"wt.exe";
    }

    std::wstring GetFallbackDirectory()
    {
        std::wstring profile = ExpandEnvironmentString(L"%USERPROFILE%");
        if (!profile.empty() && GetFileAttributesW(profile.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            return profile;
        }

        wchar_t currentDirectory[MAX_PATH]{};
        const DWORD length = GetCurrentDirectoryW(ARRAYSIZE(currentDirectory), currentDirectory);
        if (length > 0 && length < ARRAYSIZE(currentDirectory))
        {
            return currentDirectory;
        }

        return L"C:\\";
    }

    std::wstring ResolveWorkingDirectory(IShellItemArray* items)
    {
        if (!items)
        {
            return GetFallbackDirectory();
        }

        DWORD count = 0;
        if (FAILED(items->GetCount(&count)) || count == 0)
        {
            return GetFallbackDirectory();
        }

        IShellItem* item = nullptr;
        if (FAILED(items->GetItemAt(0, &item)) || !item)
        {
            return GetFallbackDirectory();
        }

        PWSTR rawPath = nullptr;
        const HRESULT hr = item->GetDisplayName(SIGDN_FILESYSPATH, &rawPath);
        item->Release();

        if (FAILED(hr) || !rawPath)
        {
            CoTaskMemFree(rawPath);
            return GetFallbackDirectory();
        }

        const DWORD attributes = GetFileAttributesW(rawPath);
        if (attributes == INVALID_FILE_ATTRIBUTES)
        {
            CoTaskMemFree(rawPath);
            return GetFallbackDirectory();
        }

        if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0 && !PathRemoveFileSpecW(rawPath))
        {
            CoTaskMemFree(rawPath);
            return GetFallbackDirectory();
        }

        std::wstring resolved(rawPath);
        CoTaskMemFree(rawPath);
        return resolved.empty() ? GetFallbackDirectory() : resolved;
    }

    std::wstring QuoteArgument(const std::wstring& argument)
    {
        std::wstring quoted;
        quoted.reserve(argument.size() + 2);
        quoted.push_back(L'"');

        size_t backslashes = 0;
        for (const wchar_t ch : argument)
        {
            if (ch == L'\\')
            {
                ++backslashes;
                continue;
            }

            if (ch == L'"')
            {
                quoted.append(backslashes * 2 + 1, L'\\');
                quoted.push_back(L'"');
                backslashes = 0;
                continue;
            }

            quoted.append(backslashes, L'\\');
            backslashes = 0;
            quoted.push_back(ch);
        }

        quoted.append(backslashes * 2, L'\\');
        quoted.push_back(L'"');
        return quoted;
    }

    HRESULT LaunchTerminal(IShellItemArray* items, bool elevated)
    {
        const std::wstring terminal = ResolveWindowsTerminalPath();
        const std::wstring directory = ResolveWorkingDirectory(items);
        const std::wstring parameters = L"-d " + QuoteArgument(directory);

        SHELLEXECUTEINFOW info{};
        info.cbSize = sizeof(info);
        info.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
        info.lpVerb = elevated ? L"runas" : L"open";
        info.lpFile = terminal.c_str();
        info.lpParameters = parameters.c_str();
        info.lpDirectory = directory.c_str();
        info.nShow = SW_SHOWNORMAL;

        if (!ShellExecuteExW(&info))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        return S_OK;
    }

    class ExplorerCommand final : public IExplorerCommand
    {
    public:
        explicit ExplorerCommand(CommandKind kind) noexcept : kind_(kind)
        {
            InterlockedIncrement(&g_objectCount);
        }

        ~ExplorerCommand()
        {
            InterlockedDecrement(&g_objectCount);
        }

        IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override
        {
            if (!result)
            {
                return E_POINTER;
            }

            *result = nullptr;
            if (IsEqualIID(iid, IID_IUnknown) || IsEqualIID(iid, IID_IExplorerCommand))
            {
                *result = static_cast<IExplorerCommand*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&referenceCount_));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const LONG remaining = InterlockedDecrement(&referenceCount_);
            if (remaining == 0)
            {
                delete this;
            }
            return static_cast<ULONG>(remaining);
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override
        {
            HRESULT hr = E_UNEXPECTED;
            switch (kind_)
            {
                case CommandKind::Parent:
                    hr = DuplicateString(L"Abrir no Terminal", name);
                    break;
                case CommandKind::Normal:
                    hr = DuplicateString(L"Normal", name);
                    break;
                case CommandKind::Elevated:
                    hr = DuplicateString(L"Elevado", name);
                    break;
            }
            return hr;
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
        {
            if (!icon)
            {
                return E_POINTER;
            }

            if (kind_ != CommandKind::Parent)
            {
                *icon = nullptr;
                return E_NOTIMPL;
            }

            const HRESULT hr = GetParentIconReference(icon);
            TraceNativeEvent(L"GetIcon", kind_, hr);
            return hr;
        }

        IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* infoTip) override
        {
            if (!infoTip)
            {
                return E_POINTER;
            }

            *infoTip = nullptr;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetCanonicalName(GUID* commandName) override
        {
            if (!commandName)
            {
                return E_POINTER;
            }

            switch (kind_)
            {
                case CommandKind::Parent:
                    *commandName = CLSID_TutzTerminalExplorerCommand;
                    break;
                case CommandKind::Normal:
                    *commandName = GUID_TutzTerminalNormal;
                    break;
                case CommandKind::Elevated:
                    *commandName = GUID_TutzTerminalElevated;
                    break;
            }

            return S_OK;
        }

        IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
        {
            if (!state)
            {
                return E_POINTER;
            }

            *state = IsCommandEnabled(kind_) ? ECS_ENABLED : ECS_HIDDEN;
            return S_OK;
        }

        IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
        {
            if (kind_ == CommandKind::Parent)
            {
                return E_NOTIMPL;
            }

            const HRESULT hr = LaunchTerminal(items, kind_ == CommandKind::Elevated);
            TraceNativeEvent(L"Invoke", kind_, hr);
            return hr;
        }

        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
        {
            if (!flags)
            {
                return E_POINTER;
            }

            if (kind_ == CommandKind::Parent)
            {
                *flags = ECF_HASSUBCOMMANDS;
            }
            else if (kind_ == CommandKind::Elevated)
            {
                *flags = ECF_HASLUASHIELD;
            }
            else
            {
                *flags = ECF_DEFAULT;
            }
            return S_OK;
        }

        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override;

    private:
        volatile LONG referenceCount_ = 1;
        CommandKind kind_;
    };

    class ExplorerCommandEnumerator final : public IEnumExplorerCommand
    {
    public:
        ExplorerCommandEnumerator() noexcept
        {
            if (IsCommandEnabled(CommandKind::Normal))
            {
                commands_[commandCount_++] = CommandKind::Normal;
            }
            if (IsCommandEnabled(CommandKind::Elevated))
            {
                commands_[commandCount_++] = CommandKind::Elevated;
            }
            InterlockedIncrement(&g_objectCount);
            TraceNativeEvent(L"EnumeratorCreated");
        }

        ExplorerCommandEnumerator(
            const std::array<CommandKind, 2>& commands,
            ULONG commandCount,
            ULONG index) noexcept
            : commands_(commands),
              commandCount_(commandCount > commands_.size()
                    ? static_cast<ULONG>(commands_.size())
                    : commandCount),
              index_(index)
        {
            if (index_ > commandCount_)
            {
                index_ = commandCount_;
            }
            InterlockedIncrement(&g_objectCount);
        }

        ~ExplorerCommandEnumerator()
        {
            InterlockedDecrement(&g_objectCount);
        }

        IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override
        {
            if (!result)
            {
                return E_POINTER;
            }

            *result = nullptr;
            if (IsEqualIID(iid, IID_IUnknown) || IsEqualIID(iid, IID_IEnumExplorerCommand))
            {
                *result = static_cast<IEnumExplorerCommand*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&referenceCount_));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const LONG remaining = InterlockedDecrement(&referenceCount_);
            if (remaining == 0)
            {
                delete this;
            }
            return static_cast<ULONG>(remaining);
        }

        IFACEMETHODIMP Next(ULONG count, IExplorerCommand** output, ULONG* fetched) override
        {
            if ((count > 0 && !output) || (count != 1 && !fetched))
            {
                TraceNativeEvent(L"EnumeratorNextInvalid", CommandKind::Parent, E_POINTER);
                return E_POINTER;
            }

            if (fetched)
            {
                *fetched = 0;
            }
            if (count == 0)
            {
                return S_OK;
            }

            for (ULONG i = 0; i < count; ++i)
            {
                output[i] = nullptr;
            }

            ULONG produced = 0;
            const ULONG originalIndex = index_;
            while (produced < count && index_ < commandCount_)
            {
                auto* command = new (std::nothrow) ExplorerCommand(commands_[index_]);
                if (!command)
                {
                    index_ = originalIndex;
                    for (ULONG i = 0; i < produced; ++i)
                    {
                        output[i]->Release();
                        output[i] = nullptr;
                    }
                    TraceNativeEvent(L"EnumeratorNextOutOfMemory", CommandKind::Parent, E_OUTOFMEMORY);
                    return E_OUTOFMEMORY;
                }

                output[produced++] = command;
                ++index_;
            }

            if (fetched)
            {
                *fetched = produced;
            }

            const HRESULT hr = produced == count ? S_OK : S_FALSE;
            TraceNativeEvent(L"EnumeratorNext", CommandKind::Parent, hr);
            return hr;
        }

        IFACEMETHODIMP Skip(ULONG count) override
        {
            const ULONG remaining = index_ < commandCount_ ? commandCount_ - index_ : 0;
            const ULONG skipped = count < remaining ? count : remaining;
            index_ += skipped;
            return skipped == count ? S_OK : S_FALSE;
        }

        IFACEMETHODIMP Reset() override
        {
            index_ = 0;
            return S_OK;
        }

        IFACEMETHODIMP Clone(IEnumExplorerCommand** clone) override
        {
            if (!clone)
            {
                return E_POINTER;
            }

            *clone = new (std::nothrow) ExplorerCommandEnumerator(commands_, commandCount_, index_);
            return *clone ? S_OK : E_OUTOFMEMORY;
        }

    private:
        volatile LONG referenceCount_ = 1;
        std::array<CommandKind, 2> commands_{};
        ULONG commandCount_ = 0;
        ULONG index_ = 0;
    };

    IFACEMETHODIMP ExplorerCommand::EnumSubCommands(IEnumExplorerCommand** commands)
    {
        if (!commands)
        {
            return E_POINTER;
        }

        *commands = nullptr;
        if (kind_ != CommandKind::Parent)
        {
            return E_NOTIMPL;
        }

        *commands = new (std::nothrow) ExplorerCommandEnumerator();
        const HRESULT hr = *commands ? S_OK : E_OUTOFMEMORY;
        TraceNativeEvent(L"EnumSubCommands", kind_, hr);
        return hr;
    }

    class ClassFactory final : public IClassFactory
    {
    public:
        ClassFactory() noexcept
        {
            InterlockedIncrement(&g_objectCount);
        }

        ~ClassFactory()
        {
            InterlockedDecrement(&g_objectCount);
        }

        IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override
        {
            if (!result)
            {
                return E_POINTER;
            }

            *result = nullptr;
            if (IsEqualIID(iid, IID_IUnknown) || IsEqualIID(iid, IID_IClassFactory))
            {
                *result = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&referenceCount_));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const LONG remaining = InterlockedDecrement(&referenceCount_);
            if (remaining == 0)
            {
                delete this;
            }
            return static_cast<ULONG>(remaining);
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** result) override
        {
            if (!result)
            {
                return E_POINTER;
            }

            *result = nullptr;
            if (outer)
            {
                return CLASS_E_NOAGGREGATION;
            }

            auto* command = new (std::nothrow) ExplorerCommand(CommandKind::Parent);
            if (!command)
            {
                TraceNativeEvent(L"CreateInstance", CommandKind::Parent, E_OUTOFMEMORY);
                return E_OUTOFMEMORY;
            }

            const HRESULT hr = command->QueryInterface(iid, result);
            command->Release();
            TraceNativeEvent(L"CreateInstance", CommandKind::Parent, hr);
            return hr;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock)
            {
                InterlockedIncrement(&g_objectCount);
            }
            else
            {
                InterlockedDecrement(&g_objectCount);
            }
            return S_OK;
        }

    private:
        volatile LONG referenceCount_ = 1;
    };
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID classId, REFIID iid, void** result)
{
    if (!result)
    {
        return E_POINTER;
    }

    *result = nullptr;
    if (!IsEqualCLSID(classId, CLSID_TutzTerminalExplorerCommand))
    {
        TraceNativeEvent(L"DllGetClassObjectWrongClass", CommandKind::Parent, CLASS_E_CLASSNOTAVAILABLE);
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) ClassFactory();
    if (!factory)
    {
        TraceNativeEvent(L"DllGetClassObject", CommandKind::Parent, E_OUTOFMEMORY);
        return E_OUTOFMEMORY;
    }

    const HRESULT hr = factory->QueryInterface(iid, result);
    factory->Release();
    TraceNativeEvent(L"DllGetClassObject", CommandKind::Parent, hr);
    return hr;
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    const LONG objects = InterlockedCompareExchange(&g_objectCount, 0, 0);
    return objects == 0 ? S_OK : S_FALSE;
}
