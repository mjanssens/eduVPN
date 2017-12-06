﻿/*
    eduVPN - End-user friendly VPN

    Copyright: 2017, The Commons Conservancy eduVPN Programme
    SPDX-License-Identifier: GPL-3.0+
*/

using eduVPN.Models;
using eduVPN.ViewModels.VPN;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace eduVPN.Views.Windows
{
    /// <summary>
    /// Interaction logic for ConnectWizard.xaml
    /// </summary>
    public partial class ConnectWizard : Window, IDisposable
    {
        #region Fields

        private System.Windows.Forms.NotifyIcon _tray_icon;
        private Dictionary<VPNSessionStatusType, Icon> _icons;
        private bool _do_close = false;
        private readonly Dictionary<string, string> _mime_types = new Dictionary<string, string>()
        {
            { ".css", "text/css" },
            { ".ico", "image/x-icon" },
        };

        /// <summary>
        /// TCP listener for OAuth authorization callback and response
        /// </summary>
        private TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);

        #endregion

        #region Properties

        /// <summary>
        /// Authorization pop-up windows
        /// </summary>
        public Dictionary<string, AuthorizationPopup> AuthorizationPopups { get; } = new Dictionary<string, AuthorizationPopup>();

        #endregion

        #region Constructors

        public ConnectWizard()
        {
            InitializeComponent();

            // Restore window position. Please mind that screen's real-estate might have changed since the previous launch.
            if (!double.IsNaN(Client.Properties.Settings.Default.WindowLeft))
                Left = Math.Min(Math.Max(Client.Properties.Settings.Default.WindowLeft, SystemParameters.VirtualScreenLeft), SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
            if (!double.IsNaN(Client.Properties.Settings.Default.WindowTop))
                Top = Math.Min(Math.Max(Client.Properties.Settings.Default.WindowTop, SystemParameters.VirtualScreenTop), SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
        }

        #endregion

        #region Methods

        /// <inheritdoc/>
        protected override void OnInitialized(EventArgs e)
        {
            // Preload icons to be used on system tray.
            var icon_size = System.Windows.Forms.SystemInformation.SmallIconSize;
            _icons = new Dictionary<VPNSessionStatusType, Icon>();
            foreach (var status_type in Enum.GetValues(typeof(VPNSessionStatusType)).Cast<VPNSessionStatusType>())
            {
                var icon_uri = new Uri(String.Format("pack://application:,,,/Resources/VPNSessionStatusTypeIcon{0}.ico", Enum.GetName(typeof(VPNSessionStatusType), status_type)));
                try { _icons.Add(status_type, new Icon(Application.GetResourceStream(icon_uri).Stream, icon_size)); }
                catch { _icons.Add(status_type, new Icon(Application.GetResourceStream(new Uri("pack://application:,,,/Resources/VPNSessionStatusTypeIconInitializing.ico")).Stream, icon_size)); }
            }

            var view_model = (ViewModels.Windows.ConnectWizard)DataContext;

            // Create notify icon, set default icon, and setup events.
            // We need to do this programatically, since System.Windows.Forms.NotifyIcon is not WPF, but borrowed from WinForms.
            _tray_icon = new System.Windows.Forms.NotifyIcon();
            _tray_icon.Icon = _icons[view_model.ActiveSession.State];
            _tray_icon.Click += TrayIcon_Click;

            // Bind to "ActiveSession.State" property to update tray icon.
            view_model.PropertyChanged += (object sender, PropertyChangedEventArgs e2) =>
                {
                    if (e2.PropertyName == nameof(view_model.ActiveSession))
                    {
                        if (view_model.ActiveSession != VPNSession.Blank)
                        {
                            // Active session changed: Bind to the session for property changes.
                            view_model.ActiveSession.PropertyChanged += (object sender_Session, PropertyChangedEventArgs e_Session) =>
                                {
                                    if (e_Session.PropertyName == nameof(view_model.ActiveSession.State))
                                        _tray_icon.Icon = _icons[view_model.ActiveSession.State];
                                };
                        }
                        else
                        {
                            // Active session ended, no more sessions: Reset tray icon to default.
                            _tray_icon.Icon = _icons[view_model.ActiveSession.State];
                        }
                    }
                };

            // Set context menu data context to allow bindings to work.
            if (Resources["SystemTrayMenu"] is ContextMenu menu)
                menu.DataContext = DataContext;

            // Launch TCP listener on the loopback interface.
            _listener.Start();
            new Thread(new ThreadStart(
                () =>
                {
                    try
                    {
                        for (; ; )
                        {
                            // Wait for the agent request and accept it.
                            TcpClient client = null;
                            try { client = _listener.AcceptTcpClient(); }
                            catch (InvalidOperationException) { break; }
                            new Thread(new ThreadStart(
                                () =>
                                {
                                    try
                                    {
                                        // Receive agent request.
                                        string request = null;
                                        using (var memory_stream = new MemoryStream())
                                        {
                                            var stream = client.GetStream();
                                            var buffer = new byte[client.ReceiveBufferSize];
                                            while (stream.DataAvailable)
                                            {
                                                // Read available data.
                                                var bytes_read = stream.Read(buffer, 0, buffer.Length);
                                                if (bytes_read == 0)
                                                    break;

                                                // Append it to the memory stream.
                                                memory_stream.Write(buffer, 0, bytes_read);
                                            }

                                            request = Encoding.UTF8.GetString(memory_stream.ToArray());
                                        }

                                        try
                                        {
                                            var request_headers = request.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                                            var request_line = request_headers[0].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                                            switch (request_line[0].ToUpperInvariant())
                                            {
                                                case "GET":
                                                case "POST":
                                                    break;
                                                default:
                                                    throw new HttpException(405, string.Format(Client.Resources.Strings.OAuthError405, request_line[0]));
                                            }

                                            var uri = new Uri(string.Format("http://{0}:{1}{2}", IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port, request_line[1]));
                                            switch (uri.AbsolutePath)
                                            {
                                                case "/callback":
                                                    {
                                                        var query = HttpUtility.ParseQueryString(uri.Query);
                                                        if (!AuthorizationPopups.TryGetValue(query["state"], out var popup))
                                                            throw new HttpException(400, Client.Resources.Strings.OAuthError400);

                                                        Dispatcher.Invoke(DispatcherPriority.Normal, (Action)(() =>
                                                        {
                                                            // Set the callback URI. This will close the pop-up.
                                                            ((ViewModels.Windows.AuthorizationPopup)popup.DataContext).CallbackURI = uri;

                                                            // (Re)activate main window.
                                                            if (!IsActive)
                                                                Show();
                                                            Topmost = true;
                                                            try
                                                            {
                                                                Activate();
                                                                Focus();
                                                            }
                                                            finally
                                                            {
                                                                Topmost = false;
                                                            }
                                                        }));

                                                        // Send response to the agent.
                                                        var response = string.Format("<!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\"/><link rel=\"stylesheet\" type=\"text/css\" href=\"/style.css\"/><title>{0}</title></head><body class=\"finished\"><table id=\"wrapper\"><tr><td><div id=\"frame\"><table><tr><td><h2>{0}</h2><p>{1}</p></td></tr></table></div></td></tr></table></body></html>",
                                                            HttpUtility.HtmlEncode(Client.Resources.Strings.OAuthFinishedTitle),
                                                            HttpUtility.HtmlEncode(Client.Resources.Strings.OAuthFinishedDescription));
                                                        using (var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)))
                                                            writer.Write(string.Format("HTTP/1.0 200 OK\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Length: {0}\r\n\r\n{1}", response.Length, response));
                                                    }
                                                    break;

                                                case "/favicon.ico":
                                                case "/style.css":
                                                    {
                                                        // Send static content.
                                                        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/OAuth" + uri.AbsolutePath)).Stream)
                                                        using (var response_stream = client.GetStream())
                                                        {
                                                            var headers = Encoding.UTF8.GetBytes(string.Format("HTTP/1.0 200 OK\r\nContent-Type: {0}\r\nContent-Length: {1}\r\n\r\n",
                                                                _mime_types[Path.GetExtension(uri.LocalPath)],
                                                                stream.Length));
                                                            response_stream.Write(headers, 0, headers.Length);
                                                            stream.CopyTo(response_stream);
                                                        }
                                                    }
                                                    break;

                                                default:
                                                    throw new HttpException(404, string.Format(Client.Resources.Strings.OAuthError404, uri.AbsolutePath));
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            // Send response to the agent.
                                            var status_code = ex is HttpException ex_http ? ex_http.GetHttpCode() : 500;
                                            var response = string.Format("<!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\"/><link rel=\"stylesheet\" type=\"text/css\" href=\"/style.css\"/><title>{0}</title></head><body class=\"error\"><table id=\"wrapper\"><tr><td><div id=\"frame\"><table><tr><td><h2>{1}</h2><p>{2}</p></td></tr></table></div></td></tr></table></body></html>",
                                                HttpUtility.HtmlEncode(Client.Resources.Strings.OAuthErrorTitle),
                                                HttpUtility.HtmlEncode(ex.Message),
                                                HttpUtility.HtmlEncode(Client.Resources.Strings.OAuthErrorDescription));
                                            using (var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)))
                                                writer.Write(string.Format("HTTP/1.0 {0} Error\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Length: {1}\r\n\r\n{2}", status_code, response.Length, response));
                                        }
                                    }
                                    catch { }
                                }
                            )).Start();
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Dispatcher.Invoke(DispatcherPriority.Normal, (Action)(() => view_model.Error = ex)); }
                })).Start();

            base.OnInitialized(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            // Stop the OAuth listener.
            _listener.Stop();

            base.OnClosed(e);
        }

        private void Hide_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void TrayIcon_Click(object sender, EventArgs ea)
        {
            if (ea is System.Windows.Forms.MouseEventArgs e_mouse)
            {
                switch (e_mouse.Button)
                {
                    case System.Windows.Forms.MouseButtons.Left:
                        // (Re)activate window.
                        if (!IsActive)
                            Show();
                        Activate();
                        Focus();
                        break;

                    case System.Windows.Forms.MouseButtons.Right:
                        // Pop-up context menu.
                        if (Resources["SystemTrayMenu"] is ContextMenu menu)
                            menu.IsOpen = true;
                        break;
                }
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Show tray icon when Connect Wizard is loaded.
            _tray_icon.Visible = true;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_do_close)
            {
                // Hide tray icon when closed.
                _tray_icon.Visible = false;

                // Save window position on closing.
                Client.Properties.Settings.Default.WindowTop = Top;
                Client.Properties.Settings.Default.WindowLeft = Left;
            }
            else
            {
                // User/system tried to close our window other than [x] button.
                // Cancel close and revert to hide.
                e.Cancel = true;
                Hide();
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            // (Re)activate window.
            if (!IsActive)
                Show();
            Activate();
            Focus();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            _do_close = true;
            Close();
        }

        /// <summary>
        /// Called when one of the ConnectWizard's instances requests user authorization
        /// </summary>
        /// <param name="sender"><c>eduVPN.ViewModels.Windows.ConnectWizard</c> requiring instance authorization</param>
        /// <param name="e">Authorization request event arguments</param>
        private void ConnectWizard_RequestInstanceAuthorization(object sender, RequestInstanceAuthorizationEventArgs e)
        {
            var view_model = new ViewModels.Windows.AuthorizationPopup(sender, e);

            // Create a new authorization pop-up.
            var popup = new AuthorizationPopup() { Owner = this, DataContext = view_model };
            view_model.PropertyChanged += (object sender2, PropertyChangedEventArgs e2) =>
            {
                if (e2.PropertyName == nameof(view_model.CallbackURI) && view_model.CallbackURI != null)
                {
                    // Close the authorization pop-up after the callback URI is set.
                    popup.DialogResult = true;
                }
            };

            // Set the redirect URI and make the final authorization URI.
            view_model.AuthorizationGrant.RedirectEndpoint = new Uri(string.Format("http://{0}:{1}/callback", IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port));
            var authorization_uri = view_model.AuthorizationGrant.AuthorizationURI;

            // Extract the state. We use it as a key to support multiple pending authorizations.
            var query = HttpUtility.ParseQueryString(authorization_uri.Query);
            AuthorizationPopups.Add(query["state"], popup);

            // Trigger authorization.
            System.Diagnostics.Process.Start(authorization_uri.ToString());

            // Run the authorization pop-up and pass the access token to be returned to the event sender.
            if (popup.ShowDialog() == true)
                e.CallbackURI = view_model.CallbackURI;

            AuthorizationPopups.Remove(query["state"]);
        }

        /// <summary>
        /// Called when an OpenVPN session requests a password authentication
        /// </summary>
        /// <param name="sender"><c>eduVPN.ViewModels.Windows.ConnectWizard</c> requiring authentication</param>
        /// <param name="e">Authentication request event arguments</param>
        private void ConnectWizard_RequestOpenVPNPasswordAuthentication(object sender, eduOpenVPN.Management.PasswordAuthenticationRequestedEventArgs e)
        {
            var view_model = new ViewModels.Windows.PasswordPopup(sender, e);

            // Create a new authentication pop-up.
            PasswordPopup popup = new PasswordPopup() { Owner = this, DataContext = view_model };

            // Set the event args to fill with data.
            popup.OK.CommandParameter = e;

            // Run the authentication pop-up and pass the credentials to be returned to the event sender.
            if (popup.ShowDialog() == true)
            {
                // Password was not set using MVVP, since <PasswordBox> control does not support binding.
                e.Password = (new NetworkCredential("", popup.Password.Password)).SecurePassword;
            }
        }

        /// <summary>
        /// Called when an OpenVPN session requests a password authentication
        /// </summary>
        /// <param name="sender"><c>eduVPN.ViewModels.Windows.ConnectWizard</c> requiring authentication</param>
        /// <param name="e">Authentication request event arguments</param>
        private void ConnectWizard_RequestOpenVPNUsernamePasswordAuthentication(object sender, eduOpenVPN.Management.UsernamePasswordAuthenticationRequestedEventArgs e)
        {
            var view_model = new ViewModels.Windows.UsernamePasswordPopup(sender, e);

            // Load previous username.
            var session = (VPNSession)sender;
            var profile_id = session.ConnectingProfile.Instance.Base.AbsoluteUri + "|" + session.ConnectingProfile.ID;
            try { view_model.Username = Client.Properties.Settings.Default.UsernameHistory[profile_id]; }
            catch { view_model.Username = null; }

            // Create a new authentication pop-up.
            UsernamePasswordPopup popup = new UsernamePasswordPopup() { Owner = this, DataContext = view_model };
            popup.Loaded += (object sender_popup, RoutedEventArgs e_popup) =>
            {
                // Set initial focus.
                if (view_model.Username == null || view_model.Username.Length == 0)
                    popup.Username.Focus();
                else
                    popup.Password.Focus();
            };

            // Set the event args to fill with data.
            popup.OK.CommandParameter = e;

            // Run the authentication pop-up and pass the credentials to be returned to the event sender.
            if (popup.ShowDialog() == true)
            {
                // Password was not set using MVVP, since <PasswordBox> control does not support binding.
                e.Password = (new NetworkCredential("", popup.Password.Password)).SecurePassword;

                // Save username for the next time.
                Client.Properties.Settings.Default.UsernameHistory[profile_id] = view_model.Username;
            }
        }

        private void ConnectWizard_RequestTwoFactorAuthentication(object sender, eduOpenVPN.Management.UsernamePasswordAuthenticationRequestedEventArgs e)
        {
            var view_model = new ViewModels.Windows.TwoFactorAuthenticationPopup(sender, e);

            // Load previous "username". 2-Factor Authentication method actually.
            var session = (VPNSession)sender;
            var profile_id = session.ConnectingProfile.Instance.Base.AbsoluteUri + "|" + session.ConnectingProfile.ID;
            var previous_method_selected = false;
            try
            {
                var method_id = Client.Properties.Settings.Default.UsernameHistory[profile_id];
                var method = view_model.MethodList.FirstOrDefault(m => m.ID == method_id);
                if (method != null)
                {
                    view_model.SelectedMethod = method;
                    previous_method_selected = true;
                }
            }
            catch { }

            // Create a new authentication pop-up.
            TwoFactorAuthenticationPopup popup = new TwoFactorAuthenticationPopup() { Owner = this, DataContext = view_model };
            popup.Loaded += (object sender_popup, RoutedEventArgs e_popup) =>
            {
                // Set initial focus.
                if (!previous_method_selected && view_model.MethodList.Count > 1)
                    popup.Method.Focus();
            };

            // Set the event args to fill with data.
            popup.OK.CommandParameter = e;

            // Run the authentication pop-up and pass the credentials to be returned to the event sender.
            if (popup.ShowDialog() == true)
            {
                // Save "username" for the next time.
                Client.Properties.Settings.Default.UsernameHistory[profile_id] = view_model.SelectedMethod.ID;
            }
        }

        private void ConnectWizard_PromptSelfUpdate(object sender, PromptSelfUpdateEventArgs e)
        {
            // Create a new prompt pop-up.
            var popup = new SelfUpdatePopup() { Owner = this };

            // Populate pop-up view model.
            var view_model = popup.DataContext as ViewModels.Windows.SelfUpdatePopup;
            view_model.InstalledVersion = e.InstalledVersion;
            view_model.AvailableVersion = e.AvailableVersion;

            // Run the pop-up and pass the selected action to be returned to the event sender.
            if (popup.ShowDialog() == true)
                e.Action = view_model.Action;
        }

        private void ConnectWizard_QuitApplication(object sender, EventArgs e)
        {
            _do_close = true;
            Close();
        }

        #endregion

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (_tray_icon != null)
                        _tray_icon.Dispose();

                    if (_icons != null)
                        foreach (var i in _icons)
                            if (i.Value != null)
                                i.Value.Dispose();
                }

                disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }
        #endregion
    }
}
