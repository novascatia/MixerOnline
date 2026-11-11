using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace MixerOnline.Controls
{
    public partial class SegmentedVuMeter : System.Windows.Controls.UserControl
    {
        private const int SegmentCount = 20;
        private readonly Border[] _leds = new Border[SegmentCount];

        // Monochrome + subtle studio peak palette (zero neon blur)
        private static readonly SolidColorBrush ClipOn = new(Color.FromRgb(239, 68, 68));     // #ef4444
        private static readonly SolidColorBrush ClipOff = new(Color.FromRgb(30, 18, 20));    // #1e1214

        private static readonly SolidColorBrush HotOn = new(Color.FromRgb(244, 244, 245));   // #f4f4f5
        private static readonly SolidColorBrush HotOff = new(Color.FromRgb(26, 26, 30));     // #1a1a1e

        private static readonly SolidColorBrush NominalOn = new(Color.FromRgb(161, 161, 170)); // #a1a1aa
        private static readonly SolidColorBrush NominalOff = new(Color.FromRgb(22, 22, 25));   // #161619

        public static readonly DependencyProperty LevelProperty =
            DependencyProperty.Register(nameof(Level), typeof(float), typeof(SegmentedVuMeter),
                new PropertyMetadata(0.0f, (d, e) => ((SegmentedVuMeter)d).UpdateLeds()));

        public static readonly DependencyProperty PeakProperty =
            DependencyProperty.Register(nameof(Peak), typeof(float), typeof(SegmentedVuMeter),
                new PropertyMetadata(0.0f, (d, e) => ((SegmentedVuMeter)d).UpdateLeds()));

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

        public SegmentedVuMeter()
        {
            InitializeComponent();
            BuildLeds();
        }

        private void BuildLeds()
        {
            LedItemsControl.Items.Clear();

            for (int i = 0; i < SegmentCount; i++)
            {
                var border = new Border
                {
                    Margin = new Thickness(0, 1, 0, 1),
                    CornerRadius = new CornerRadius(1),
                    Background = GetOffBrush(i)
                };
                _leds[i] = border;
                LedItemsControl.Items.Add(border);
            }
        }

        private static Brush GetOffBrush(int index)
        {
            if (index < 2) return ClipOff;
            if (index < 7) return HotOff;
            return NominalOff;
        }

        private static Brush GetOnBrush(int index)
        {
            if (index < 2) return ClipOn;
            if (index < 7) return HotOn;
            return NominalOn;
        }

        private void UpdateLeds()
        {
            float normLevel = Math.Clamp(Level, 0f, 1f);
            float normPeak = Math.Clamp(Peak, 0f, 1f);

            double rmsPower = Math.Pow(normLevel, 0.45);
            double peakPower = Math.Pow(normPeak, 0.45);

            int litSegments = (int)Math.Round(rmsPower * SegmentCount);
            int peakSegment = (int)Math.Round(peakPower * SegmentCount) - 1;

            for (int i = 0; i < SegmentCount; i++)
            {
                int fromBottom = SegmentCount - 1 - i;

                bool isLit = fromBottom < litSegments;
                bool isPeakHold = fromBottom == peakSegment && peakSegment >= litSegments;

                _leds[i].Background = (isLit || isPeakHold) ? GetOnBrush(i) : GetOffBrush(i);
            }
        }
    }
}
