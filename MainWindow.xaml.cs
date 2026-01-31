using pdf_chat_app.ViewModels;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace pdf_chat_app
{
    public partial class MainWindow : Window
    {
        private bool _webViewReady;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm == null) return;

            // Auto-scroll when messages change
            vm.Messages.CollectionChanged += Messages_CollectionChanged;

            // React when SelectedPdfPath changes
            vm.PropertyChanged += Vm_PropertyChanged;

            // If you named your TabControl in XAML as SourceTabs, hook selection change
            // (If you didn't name it yet, see note below)
            if (SourceTabs != null)
                SourceTabs.SelectionChanged += SourceTabs_SelectionChanged;

            // Init WebView2 (safe if runtime missing)
            try
            {
                if (PdfViewer != null)
                {
                    await PdfViewer.EnsureCoreWebView2Async();
                    _webViewReady = PdfViewer.CoreWebView2 != null;
                }
            }
            catch
            {
                _webViewReady = false; // chat still works
            }

            // If PDF tab already selected, try load
            if (IsPdfTabSelected())
                TryNavigatePdf(vm.SelectedPdfPath);
        }

        private void Messages_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ChatScroll != null)
                    ChatScroll.ScrollToEnd();
            }), DispatcherPriority.Background);
        }

        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "SelectedPdfPath")
            {
                // Only navigate if PDF tab is active
                if (!IsPdfTabSelected()) return;

                var vm = DataContext as MainViewModel;
                if (vm == null) return;

                TryNavigatePdf(vm.SelectedPdfPath);
            }
        }

        private void SourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // When user switches to PDF tab, load the PDF if there is one
            if (!IsPdfTabSelected()) return;

            var vm = DataContext as MainViewModel;
            if (vm == null) return;

            TryNavigatePdf(vm.SelectedPdfPath);
        }

        private bool IsPdfTabSelected()
        {
            // We assume TabControl named SourceTabs and PDF tab is index 0.
            // If you use different ordering, adjust this.
            if (SourceTabs == null) return true;
            return SourceTabs.SelectedIndex == 0;
        }

        private void TryNavigatePdf(string path)
        {
            try
            {
                if (!_webViewReady) return;
                if (PdfViewer == null) return;

                if (string.IsNullOrWhiteSpace(path)) return;
                if (path == "(no file selected)") return;
                if (!File.Exists(path)) return;

                // Use file:/// URL (more reliable than new Uri(path))
                var fileUri = new Uri(path).AbsoluteUri;

                // Navigate via CoreWebView2 if available
                if (PdfViewer.CoreWebView2 != null)
                {
                    PdfViewer.CoreWebView2.Navigate(fileUri);
                }
                else
                {
                    PdfViewer.Source = new Uri(fileUri);
                }
            }
            catch
            {
                // ignore viewer errors
            }
        }

        private void ChatInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            // Shift+Enter = newline
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                return;

            // Enter = send
            e.Handled = true;

            var vm = DataContext as MainViewModel;
            if (vm == null) return;

            if (vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);
        }
    }
}
