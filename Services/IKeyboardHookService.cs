// Services/IKeyboardHookService.cs
using System;

namespace TutzApp.Services
{
    public interface IKeyboardHookService
    {
        event Action? HelpRequested;
        void StartHook();
        void StopHook();
    }
}
