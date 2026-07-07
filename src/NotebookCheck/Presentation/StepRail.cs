using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NotebookCheck.Presentation;

/// <summary>
/// Trilha vertical de etapas exibida na sidebar. Marca a etapa anterior como
/// concluída, a atual como ativa e as próximas como pendentes. Cada item é
/// clicável e dispara <see cref="StepClickedCommand"/> com o
/// <see cref="WizardStep"/> correspondente como parâmetro.
/// </summary>
public sealed class StepRail : ContentControl
{
    public static readonly DependencyProperty CurrentStepProperty =
        DependencyProperty.Register(
            nameof(CurrentStep),
            typeof(WizardStep),
            typeof(StepRail),
            new FrameworkPropertyMetadata(
                WizardStep.Start,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnCurrentStepChanged));

    public WizardStep CurrentStep
    {
        get => (WizardStep)GetValue(CurrentStepProperty);
        set => SetValue(CurrentStepProperty, value);
    }

    /// <summary>
    /// Maior etapa já alcançada. Usado para habilitar saltos para etapas
    /// avançadas após o técnico ter voltado a uma anterior. Não-clicáveis
    /// quando ainda não foram alcançadas.
    /// </summary>
    public static readonly DependencyProperty HighWaterMarkProperty =
        DependencyProperty.Register(
            nameof(HighWaterMark),
            typeof(int),
            typeof(StepRail),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnCurrentStepChanged));

    public int HighWaterMark
    {
        get => (int)GetValue(HighWaterMarkProperty);
        set => SetValue(HighWaterMarkProperty, value);
    }

    public static readonly DependencyProperty StepClickedCommandProperty =
        DependencyProperty.Register(
            nameof(StepClickedCommand),
            typeof(ICommand),
            typeof(StepRail),
            new PropertyMetadata(null));

    public ICommand? StepClickedCommand
    {
        get => (ICommand?)GetValue(StepClickedCommandProperty);
        set => SetValue(StepClickedCommandProperty, value);
    }

    private static readonly (WizardStep step, string label)[] Steps =
    {
        (WizardStep.Start, "Início"),
        (WizardStep.ModeSelect, "Modo do checklist"),
        (WizardStep.Identification, "Identificação"),
        (WizardStep.Hardware, "Hardware"),
        (WizardStep.AutoTests, "Testes automáticos"),
        (WizardStep.Performance, "Desempenho e stress"),
        (WizardStep.Inputs, "Inputs e portas"),
        (WizardStep.Manual, "Inspeção física"),
        (WizardStep.Summary, "Resumo"),
        (WizardStep.Done, "Concluído"),
    };

    public StepRail()
    {
        Focusable = false;
        Loaded += (_, _) => Rebuild();
    }

    private static void OnCurrentStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StepRail r) r.Rebuild();
    }

    private void Rebuild()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var brand = (Brush)app.FindResource("BrandBrush");
        var ink100 = (Brush)app.FindResource("Ink100Brush");
        var ink200 = (Brush)app.FindResource("Ink200Brush");
        var ink400 = (Brush)app.FindResource("Ink400Brush");
        var ink700 = (Brush)app.FindResource("Ink700Brush");
        var ink900 = (Brush)app.FindResource("Ink900Brush");
        var ok = (Brush)app.FindResource("OkBrush");

        var currentIndex = Array.FindIndex(Steps, s => s.step == CurrentStep);

        var root = new StackPanel();

        for (var i = 0; i < Steps.Length; i++)
        {
            var (step, label) = Steps[i];
            var isCurrent = i == currentIndex;
            var isPast = i < currentIndex;
            // Todas as etapas são clicáveis. Restrições semânticas (ex.: tentar
            // acessar Summary sem identificação) são tratadas no ViewModel,
            // onde apenas avisamos sem bloquear.
            var isReachable = true;

            var btn = new Button
            {
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                MinWidth = 0,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsEnabled = isReachable,
                Focusable = isReachable,
                Command = StepClickedCommand,
                CommandParameter = step,
                Template = BuildButtonTemplate(),
            };

            var item = new Grid();
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            item.ColumnDefinitions.Add(new ColumnDefinition());

            var bulletPanel = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
            var bullet = new Ellipse
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(0, 6, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill = isPast ? ok : (isCurrent ? brand : Brushes.White),
                Stroke = isPast ? ok : (isCurrent ? brand : ink200),
                StrokeThickness = 2,
            };
            bulletPanel.Children.Add(bullet);

            if (i < Steps.Length - 1)
            {
                var line = new Rectangle
                {
                    Width = 2,
                    Margin = new Thickness(0, 22, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Fill = isPast ? ok : ink200,
                };
                bulletPanel.Children.Add(line);
            }
            Grid.SetColumn(bulletPanel, 0);
            item.Children.Add(bulletPanel);

            var text = new TextBlock
            {
                Text = label,
                Foreground = isCurrent ? ink900 : (isReachable ? ink700 : ink400),
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = 13,
                Margin = new Thickness(8, 4, 0, 12),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(text, 1);
            item.Children.Add(text);

            btn.Content = item;
            root.Children.Add(btn);
        }

        Content = root;
    }

    private static ControlTemplate BuildButtonTemplate()
    {
        // Template mínimo que destaca hover sem alterar o layout.
        var border = new FrameworkElementFactory(typeof(Border), "PART_Border");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
        border.SetValue(Border.MarginProperty, new Thickness(-6, 0, -6, 0));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter
        {
            TargetName = "PART_Border",
            Property = Border.BackgroundProperty,
            Value = System.Windows.Application.Current?.FindResource("Ink100Brush") ?? Brushes.WhiteSmoke,
        });
        template.Triggers.Add(hoverTrigger);

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter { Property = UIElement.OpacityProperty, Value = 0.6 });
        template.Triggers.Add(disabledTrigger);

        return template;
    }
}
