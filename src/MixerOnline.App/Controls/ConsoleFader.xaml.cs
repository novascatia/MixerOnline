using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MixerOnline.Controls
{
    public partial class ConsoleFader : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(float), typeof(ConsoleFader),
                new FrameworkPropertyMetadata(1.0f, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        private static readonly System.Windows.Media.SolidColorBrush MutedBrush =
            new(System.Windows.Media.Color.FromRgb(113, 113, 122)); // #71717a
        private static readonly System.Windows.Media.SolidColorBrush UnityBrush =
            new(System.Windows.Media.Color.FromRgb(244, 244, 245)); // #f4f4f5
        private static readonly System.Windows.Media.SolidColorBrush NormalBrush =
            new(System.Windows.Media.Color.FromRgb(161, 161, 170)); // #a1a1aa

        public float Value
        {
            get => (float)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public ConsoleFader()
        {
            InitializeComponent();

            FaderSlider.ValueChanged += (s, e) =>
            {
                Value = (float)e.NewValue;
                UpdateDbText(Value);
            };
        }

        private void OnSliderDoubleClicked(object sender, MouseButtonEventArgs e)
        {
            Value = 1.0f;
            FaderSlider.Value = 1.0;
        }

        private void OnReadoutClicked(object sender, MouseButtonEventArgs e)
        {
            Value = 1.0f;
            FaderSlider.Value = 1.0;
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ConsoleFader fader)
            {
                float val = Math.Clamp((float)e.NewValue, 0f, 1.5f);
                if (Math.Abs(fader.FaderSlider.Value - val) > 0.001)
                {
                    fader.FaderSlider.Value = val;
                }
                fader.UpdateDbText(val);
            }
        }

        private void UpdateDbText(float val)
        {
            if (val <= 0.001f)
            {
                TxtDb.Text = "-∞ dB";
                TxtDb.Foreground = MutedBrush;
            }
            else
            {
                double db = 20.0 * Math.Log10(val);
                if (Math.Abs(db) < 0.08)
                {
                    TxtDb.Text = "0.0 dB";
                    TxtDb.Foreground = UnityBrush;
                }
                else if (db > 0)
                {
                    TxtDb.Text = $"+{db:F1} dB";
                    TxtDb.Foreground = UnityBrush;
                }
                else
                {
                    TxtDb.Text = $"{db:F1} dB";
                    TxtDb.Foreground = NormalBrush;
                }
            }
        }
    }
}
