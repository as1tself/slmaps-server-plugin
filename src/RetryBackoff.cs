namespace SlmapsServerPlugin
{
    internal sealed class RetryBackoff
    {
        private static readonly int[] StepsSeconds = { 5, 15, 60, 300 };

        private int _failures;

        public int Failures
        {
            get { return _failures; }
        }

        public int NextDelaySeconds()
        {
            int index = _failures < StepsSeconds.Length ? _failures : StepsSeconds.Length - 1;
            if (_failures < int.MaxValue)
            {
                _failures++;
            }
            return StepsSeconds[index];
        }

        public void Reset()
        {
            _failures = 0;
        }
    }
}
