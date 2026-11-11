using System.Windows;
using System.Windows.Controls;

namespace MixerOnline.Controls
{
    public partial class StereoVuMeter : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty LevelLeftProperty =
            DependencyProperty.Register(nameof(LevelLeft), typeof(float), typeof(StereoVuMeter),
                new PropertyMetadata(0.0f, (d, e) => ((StereoVuMeter)d).MeterL.Level = (float)e.NewValue));

        public static readonly DependencyProperty PeakLeftProperty =
            DependencyProperty.Register(nameof(PeakLeft), typeof(float), typeof(StereoVuMeter),
                new PropertyMetadata(0.0f, (d, e) => ((StereoVuMeter)d).MeterL.Peak = (float)e.NewValue));

        public static readonly DependencyProperty LevelRightProperty =
            DependencyProperty.Register(nameof(LevelRight), typeof(float), typeof(StereoVuMeter),
                new PropertyMetadata(0.0f, (d, e) => ((StereoVuMeter)d).MeterR.Level = (float)e.NewValue));

        public static readonly DependencyProperty PeakRightProperty =
            DependencyProperty.Register(nameof(PeakRight), typeof(float), typeof(StereoVuMeter),
                new PropertyMetadata(0.0f, (d, e) => ((StereoVuMeter)d).MeterR.Peak = (float)e.NewValue));

        public float LevelLeft
        {
            get => (float)GetValue(LevelLeftProperty);
            set => SetValue(LevelLeftProperty, value);
        }

        public float PeakLeft
        {
            get => (float)GetValue(PeakLeftProperty);
            set => SetValue(PeakLeftProperty, value);
        }

        public float LevelRight
        {
            get => (float)GetValue(LevelRightProperty);
            set => SetValue(LevelRightProperty, value);
        }

        public float PeakRight
        {
            get => (float)GetValue(PeakRightProperty);
            set => SetValue(PeakRightProperty, value);
        }

        public StereoVuMeter()
        {
            InitializeComponent();
        }
    }
}
