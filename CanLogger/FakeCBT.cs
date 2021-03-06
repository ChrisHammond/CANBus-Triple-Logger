﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CanBusTriple;

namespace CanLogger
{
    // ReSharper disable once InconsistentNaming
    public class FakeCBT
    {
        public event CanMessageReceivedHandler CanMessageReceived;
        private readonly MicroTimer _timer;
        private readonly Random _rnd;
        private int _counter;

        private DateTime _startTime;
        private Stopwatch _stopwatch;

        public FakeCBT(string portName = null)
        {
            _rnd = new Random();
            _timer = new MicroTimer(1000);
            _timer.MicroTimerElapsed += (s, args) => {
                if (CanMessageReceived == null) return;
                // Generate random Can Message
                var msg = new CanMessage {
                    Bus = 1,
                    Id = _rnd.Next(1, 0xFFF),
                    Status = _rnd.Next(0, 3),
                    Data = BitConverter.GetBytes(++_counter),
                    DateTime = _startTime + _stopwatch.Elapsed
                };
                CanMessageReceived(msg);
            };
        }


        public bool Connected
        {
            get;
            private set;
        }

        public bool Busy
        {
            get { return _timer.Enabled;  }
        }

        public void Connect()
        {
            Connected = true;
        }

        public void Disconnect()
        {
            Connected = false;
        }

        public void CancelCommand(bool closePort)
        {
            DisableLog(0);
            if (closePort) Connected = false;
        }

        public void EnableLogWithMask(int bus, int msgFilter1, int mask1)
        {
            EnableLog(bus);
        }

        public void EnableLog(int bus)
        {
            _startTime = DateTime.Now;
            _stopwatch = Stopwatch.StartNew();
            _timer.Start();
        }

        public void DisableLog(int bus)
        {
            _timer.Stop();
            _stopwatch.Stop();
        }

        public Dictionary<string, string> GetSystemInfo()
        {
            return new Dictionary<string, string>(2) {
                { "name", "Fake CANBus Triple" }, { "version", "0.1" }
            };
        }
    }

    /// <summary>
    /// MicroStopwatch class
    /// </summary>
    public class MicroStopwatch : Stopwatch
    {
        readonly double _microSecPerTick =
            1000000D / Frequency;

        public MicroStopwatch()
        {
            if (!IsHighResolution) {
                throw new Exception("On this system the high-resolution " +
                                    "performance counter is not available");
            }
        }

        public long ElapsedMicroseconds
        {
            get
            {
                return (long)(ElapsedTicks * _microSecPerTick);
            }
        }
    }

    /// <summary>
    /// MicroTimer class
    /// </summary>
    public class MicroTimer
    {
        public delegate void MicroTimerElapsedEventHandler(
                             object sender,
                             MicroTimerEventArgs timerEventArgs);
        public event MicroTimerElapsedEventHandler MicroTimerElapsed;

        Thread _threadTimer;
        long _ignoreEventIfLateBy = long.MaxValue;
        long _timerIntervalInMicroSec;
        bool _stopTimer = true;

        public MicroTimer()
        {
        }

        public MicroTimer(long timerIntervalInMicroseconds)
        {
            Interval = timerIntervalInMicroseconds;
        }

        public long Interval
        {
            get
            {
                return Interlocked.Read(
                    ref _timerIntervalInMicroSec);
            }
            set
            {
                Interlocked.Exchange(
                    ref _timerIntervalInMicroSec, value);
            }
        }

        public long IgnoreEventIfLateBy
        {
            get
            {
                return Interlocked.Read(
                    ref _ignoreEventIfLateBy);
            }
            set
            {
                Interlocked.Exchange(
                    ref _ignoreEventIfLateBy, value <= 0 ? long.MaxValue : value);
            }
        }

        public bool Enabled
        {
            set
            {
                if (value) {
                    Start();
                }
                else {
                    Stop();
                }
            }
            get
            {
                return (_threadTimer != null && _threadTimer.IsAlive);
            }
        }

        public void Start()
        {
            if (Enabled || Interval <= 0) {
                return;
            }

            _stopTimer = false;

            ThreadStart threadStart = delegate
            {
                NotificationTimer(ref _timerIntervalInMicroSec,
                                  ref _ignoreEventIfLateBy,
                                  ref _stopTimer);
            };

            _threadTimer = new Thread(threadStart);
            _threadTimer.Priority = ThreadPriority.Highest;
            _threadTimer.Start();
        }

        public void Stop()
        {
            _stopTimer = true;
        }

        public void StopAndWait()
        {
            StopAndWait(Timeout.Infinite);
        }

        public bool StopAndWait(int timeoutInMilliSec)
        {
            _stopTimer = true;

            if (!Enabled || _threadTimer.ManagedThreadId ==
                Thread.CurrentThread.ManagedThreadId) {
                return true;
            }

            return _threadTimer.Join(timeoutInMilliSec);
        }

        public void Abort()
        {
            _stopTimer = true;

            if (Enabled) {
                _threadTimer.Abort();
            }
        }

        void NotificationTimer(ref long timerIntervalInMicroSec,
                               ref long ignoreEventIfLateBy,
                               ref bool stopTimer)
        {
            int timerCount = 0;
            long nextNotification = 0;

            MicroStopwatch microStopwatch = new MicroStopwatch();
            microStopwatch.Start();

            while (!stopTimer) {
                long callbackFunctionExecutionTime =
                    microStopwatch.ElapsedMicroseconds - nextNotification;

                long timerIntervalInMicroSecCurrent =
                    Interlocked.Read(ref timerIntervalInMicroSec);
                long ignoreEventIfLateByCurrent =
                    Interlocked.Read(ref ignoreEventIfLateBy);

                nextNotification += timerIntervalInMicroSecCurrent;
                timerCount++;
                long elapsedMicroseconds = 0;

                while ((elapsedMicroseconds = microStopwatch.ElapsedMicroseconds)
                        < nextNotification) {
                    Thread.SpinWait(10);
                }

                long timerLateBy = elapsedMicroseconds - nextNotification;

                if (timerLateBy >= ignoreEventIfLateByCurrent) {
                    continue;
                }

                MicroTimerEventArgs microTimerEventArgs =
                     new MicroTimerEventArgs(timerCount,
                                             elapsedMicroseconds,
                                             timerLateBy,
                                             callbackFunctionExecutionTime);
                MicroTimerElapsed(this, microTimerEventArgs);
            }

            microStopwatch.Stop();
        }
    }

    /// <summary>
    /// MicroTimer Event Argument class
    /// </summary>
    public class MicroTimerEventArgs : EventArgs
    {
        // Simple counter, number times timed event (callback function) executed
        public int TimerCount { get; private set; }

        // Time when timed event was called since timer started
        public long ElapsedMicroseconds { get; private set; }

        // How late the timer was compared to when it should have been called
        public long TimerLateBy { get; private set; }

        // Time it took to execute previous call to callback function (OnTimedEvent)
        public long CallbackFunctionExecutionTime { get; private set; }

        public MicroTimerEventArgs(int timerCount,
                                   long elapsedMicroseconds,
                                   long timerLateBy,
                                   long callbackFunctionExecutionTime)
        {
            TimerCount = timerCount;
            ElapsedMicroseconds = elapsedMicroseconds;
            TimerLateBy = timerLateBy;
            CallbackFunctionExecutionTime = callbackFunctionExecutionTime;
        }
    }
}
