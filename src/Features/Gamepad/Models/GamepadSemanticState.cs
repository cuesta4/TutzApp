namespace TutzApp.Models
{
    /// <summary>
    /// Represents a normalized semantic snapshot of a physical gamepad's state,
    /// including dedicated Guide (Xbox/PS/Home) and Share/Capture buttons.
    /// </summary>
    public readonly struct GamepadSemanticState
    {
        public ushort Buttons { get; }
        public double LeftX { get; }
        public double LeftY { get; }
        public double RightX { get; }
        public double RightY { get; }
        public double LeftTrigger { get; }
        public double RightTrigger { get; }
        public bool IsGuideDown { get; }
        public bool IsShareDown { get; }
        public long TimestampTick { get; }

        public GamepadSemanticState(
            ushort buttons,
            double leftX,
            double leftY,
            double rightX,
            double rightY,
            double leftTrigger,
            double rightTrigger,
            bool isGuideDown,
            bool isShareDown,
            long timestampTick)
        {
            Buttons = buttons;
            LeftX = leftX;
            LeftY = leftY;
            RightX = rightX;
            RightY = rightY;
            LeftTrigger = leftTrigger;
            RightTrigger = rightTrigger;
            IsGuideDown = isGuideDown;
            IsShareDown = isShareDown;
            TimestampTick = timestampTick;
        }

        public static GamepadSemanticState Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, false, false, 0);

        public bool IsNeutral =>
            Buttons == 0 &&
            !IsGuideDown &&
            !IsShareDown &&
            System.Math.Abs(LeftX) < 0.15 &&
            System.Math.Abs(LeftY) < 0.15 &&
            System.Math.Abs(RightX) < 0.15 &&
            System.Math.Abs(RightY) < 0.15 &&
            LeftTrigger < 0.15 &&
            RightTrigger < 0.15;
    }
}
