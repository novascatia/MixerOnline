using System;
using System.Windows;
using System.Windows.Controls;

namespace MixerOnline.Controls
{
    public partial class VuMeter : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty LevelProperty =
            DependencyProperty.Register(nameof(Level), typeof(float), typeof(VuMeter),
                new PropertyMetadata(0.0f, OnLevelChanged));

        public static readonly DependencyProperty PeakProperty =
            DependencyProperty.Register(nameof(Peak), typeof(float), typeof(VuMeter),
                new PropertyMetadata(0.0f, OnPeakChanged));

        public float Level
        {
            get => (float)GetValue(LevelProperty);
            set => SetValue(LevelProperty, value);
        }

        public float Peak
        {
            get => (float)GetValue(PeakProperty);
            set => SetValue(PeakProperty, value);
        }

        public VuMeter()
        {
            InitializeComponent();
            SizeChanged += (s, e) => UpdatePeakPosition();
        }

        private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is VuMeter meter)
            {
                float val = Math.Clamp((float)e.NewValue, 0f, 1f);
                // Convert linear scale to perceptual log / decibel-like curve for natural VU response
                // Linear 0.01 -> -40dB, 0.1 -> -20dB, 1.0 -> 0dB
                double displayVal = Math.Pow(val, 0.5); // Square-root expansion for musical readability
                meter.RmsBar.Value = displayVal;
            }
        }

        private static void OnPeakChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is VuMeter meter)
            {
                meter.UpdatePeakPosition();
            }
        }

        private void UpdatePeakPosition()
        {
            float peak = Math.Clamp(Peak, 0f, 1f);
            if (peak > 0.01f && ActualWidth > 4)
            {
                PeakLine.Visibility = Visibility.Visible;
                double displayVal = Math.Pow(peak, 0.5);
                double leftPos = displayVal * (ActualWidth - 6);
                Canvas.SetLeft(PeakLine, Math.Max(0, leftPos));
            }
            else
            {
                PeakLine.Visibility = Visibility.Collapsed;
            }
        }
    }
}
