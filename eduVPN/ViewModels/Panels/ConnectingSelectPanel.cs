﻿/*
    eduVPN - End-user friendly VPN

    Copyright: 2017, The Commons Conservancy eduVPN Programme
    SPDX-License-Identifier: GPL-3.0+
*/

using eduVPN.Models;
using eduVPN.ViewModels.Windows;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace eduVPN.ViewModels.Panels
{
    /// <summary>
    /// Connecting instance and/or profile select panel base class
    /// </summary>
    public class ConnectingSelectPanel : BindableBase
    {
        #region Properties

        /// <summary>
        /// The page parent
        /// </summary>
        public ConnectWizard Parent { get; }

        /// <summary>
        /// Selected instance source type
        /// </summary>
        public InstanceSourceType InstanceSourceType { get; }

        /// <summary>
        /// Selected instance source
        /// </summary>
        public InstanceSource InstanceSource
        {
            get { return Parent.InstanceSources[(int)InstanceSourceType]; }
        }

        /// <summary>
        /// Selected instance
        /// </summary>
        /// <remarks><c>null</c> if none selected.</remarks>
        public Instance SelectedInstance
        {
            get { return _selected_instance; }
            set { SetProperty(ref _selected_instance, value); }
        }
        private Instance _selected_instance;

        /// <summary>
        /// Set connecting instance command
        /// </summary>
        public DelegateCommand SetConnectingInstance
        {
            get
            {
                if (_set_connecting_instance == null)
                {
                    _set_connecting_instance = new DelegateCommand(
                        // execute
                        () =>
                        {
                            Parent.ChangeTaskCount(+1);
                            try
                            {
                                Parent.InstanceSourceType = InstanceSourceType;
                                InstanceSource.ConnectingInstance = SelectedInstance;

                                // Go to profile selection page.
                                Parent.CurrentPage = Parent.ConnectingProfileSelectPage;
                            }
                            catch (Exception ex) { Parent.Error = ex; }
                            finally { Parent.ChangeTaskCount(-1); }
                        },

                        // canExecute
                        () => SelectedInstance != null);

                    // Setup canExecute refreshing.
                    PropertyChanged += (object sender, PropertyChangedEventArgs e) => { if (e.PropertyName == nameof(SelectedInstance)) _set_connecting_instance.RaiseCanExecuteChanged(); };
                }

                return _set_connecting_instance;
            }
        }
        private DelegateCommand _set_connecting_instance;

        /// <summary>
        /// Menu label for <c>ForgetSelectedInstance</c> command
        /// </summary>
        public string ForgetSelectedInstanceLabel
        {
            get { return string.Format(Resources.Strings.InstanceForget, SelectedInstance); }
        }

        /// <summary>
        /// Forget selected instance command
        /// </summary>
        public DelegateCommand ForgetSelectedInstance
        {
            get
            {
                if (_forget_selected_instance == null)
                {
                    _forget_selected_instance = new DelegateCommand(
                        // execute
                        () =>
                        {
                            Parent.ChangeTaskCount(+1);
                            try
                            {
                                ForgetInstance(SelectedInstance);

                                // Return to starting page. Should the abscence of configurations from history resolve in different starting page of course.
                                if (Parent.StartingPage != Parent.CurrentPage)
                                    Parent.CurrentPage = Parent.StartingPage;
                            }
                            catch (Exception ex) { Parent.Error = ex; }
                            finally { Parent.ChangeTaskCount(-1); }
                        },

                        // canExecute
                        () =>
                            InstanceSource is LocalInstanceSource &&
                            SelectedInstance != null &&
                            InstanceSource.ConnectingInstanceList.IndexOf(SelectedInstance) >= 0 &&
                            !Parent.Sessions.Any(session => session.ConnectingProfile.Instance.Equals(SelectedInstance)));

                    // Setup canExecute refreshing.
                    PropertyChanged += (object sender, PropertyChangedEventArgs e) => { if (e.PropertyName == nameof(SelectedInstance)) _forget_selected_instance.RaiseCanExecuteChanged(); };
                    InstanceSource.ConnectingInstanceList.CollectionChanged += (object sender, NotifyCollectionChangedEventArgs e) => _forget_selected_instance.RaiseCanExecuteChanged();
                    Parent.Sessions.CollectionChanged += (object sender, NotifyCollectionChangedEventArgs e) => _forget_selected_instance.RaiseCanExecuteChanged();
                }

                return _forget_selected_instance;
            }
        }
        private DelegateCommand _forget_selected_instance;

        /// <summary>
        /// Currently selected profile
        /// </summary>
        public Profile SelectedProfile
        {
            get { return _selected_profile; }
            set { SetProperty(ref _selected_profile, value); }
        }
        private Profile _selected_profile;

        /// <summary>
        /// Connect selected profile command
        /// </summary>
        public DelegateCommand ConnectSelectedProfile
        {
            get
            {
                if (_connect_selected_profile == null)
                {
                    _connect_selected_profile = new DelegateCommand(
                        // execute
                        async () =>
                        {
                            Parent.ChangeTaskCount(+1);
                            try
                            {
                                if (SelectedProfile.IsTwoFactorAuthentication)
                                {
                                    // Selected profile requires 2-Factor Authentication.
                                    var authenticating_instance = InstanceSource is LocalInstanceSource ? SelectedProfile.Instance : InstanceSource.AuthenticatingInstance;

                                    // Get user info.
                                    var userinfo_task = new Task<UserInfo>(() => authenticating_instance.GetUserInfo(authenticating_instance, Window.Abort.Token), TaskCreationOptions.LongRunning);
                                    userinfo_task.Start();
                                    var user_info = await userinfo_task;

                                    if (!user_info.IsTwoFactorAuthentication ||
                                        user_info.TwoFactorMethods != TwoFactorAuthenticationMethods.None && (user_info.TwoFactorMethods & SelectedProfile.TwoFactorMethods) == 0)
                                    {
                                        // User is not enrolled for 2FA, or is not enrolled using any required 2FA method.

                                        // Get authenticating instance endpoints. (Already cached by GetUserInfo above.)
                                        var api = authenticating_instance.GetEndpoints(Window.Abort.Token);

                                        // Offer user to enroll for 2FA.
                                        Parent.Profile_RequestTwoFactorEnrollment(
                                            this,
                                            new RequestTwoFactorEnrollmentEventArgs(
                                                api.TwoFactorAuthenticationEnroll ?? authenticating_instance.Base,
                                                SelectedProfile));
                                        return;
                                    }
                                }

                                // Set connecting instance.
                                Parent.InstanceSourceType = InstanceSourceType;
                                InstanceSource.ConnectingInstance = SelectedProfile.Instance;

                                // Start VPN session.
                                var param = new ConnectWizard.StartSessionParams(InstanceSourceType, SelectedProfile);
                                if (Parent.StartSession.CanExecute(param))
                                    Parent.StartSession.Execute(param);
                            }
                            catch (Exception ex) { Parent.Error = ex; }
                            finally { Parent.ChangeTaskCount(-1); }
                        },

                        // canExecute
                        () => SelectedProfile != null);

                    // Setup canExecute refreshing.
                    PropertyChanged += (object sender, PropertyChangedEventArgs e) => { if (e.PropertyName == nameof(SelectedProfile)) _connect_selected_profile.RaiseCanExecuteChanged(); };
                }

                return _connect_selected_profile;
            }
        }
        private DelegateCommand _connect_selected_profile;

        /// <summary>
        /// Menu label for <c>ForgetSelectedProfile</c> command
        /// </summary>
        public string ForgetSelectedProfileLabel
        {
            get { return string.Format(Resources.Strings.InstanceForget, SelectedProfile); }
        }

        /// <summary>
        /// Forget selected profile command
        /// </summary>
        public DelegateCommand ForgetSelectedProfile
        {
            get
            {
                if (_forget_selected_profile == null)
                {
                    _forget_selected_profile = new DelegateCommand(
                        // execute
                        () =>
                        {
                            Parent.ChangeTaskCount(+1);
                            try
                            {
                                if (InstanceSource is LocalInstanceSource instance_source_local)
                                {
                                    // Remove the profile from history.
                                    var profile = SelectedProfile;
                                    var instance = profile.Instance;
                                    var forget_instance = true;
                                    for (var i = instance_source_local.ConnectingProfileList.Count; i-- > 0;)
                                        if (instance_source_local.ConnectingProfileList[i].Equals(profile))
                                            instance_source_local.ConnectingProfileList.RemoveAt(i);
                                        else if (instance_source_local.ConnectingProfileList[i].Instance.Equals(instance))
                                            forget_instance = false;

                                    if (forget_instance)
                                        ForgetInstance(instance);

                                    // Return to starting page. Should the abscence of configurations from history resolve in different starting page of course.
                                    if (Parent.StartingPage != Parent.CurrentPage)
                                        Parent.CurrentPage = Parent.StartingPage;
                                }
                            }
                            catch (Exception ex) { Parent.Error = ex; }
                            finally { Parent.ChangeTaskCount(-1); }
                        },

                        // canExecute
                        () =>
                            InstanceSource is LocalInstanceSource instance_source_local &&
                            SelectedProfile != null &&
                            instance_source_local.ConnectingProfileList.IndexOf(SelectedProfile) >= 0 &&
                            !Parent.Sessions.Any(session => session.ConnectingProfile.Equals(SelectedProfile)));

                    // Setup canExecute refreshing.
                    PropertyChanged += (object sender, PropertyChangedEventArgs e) => { if (e.PropertyName == nameof(SelectedProfile)) _forget_selected_profile.RaiseCanExecuteChanged(); };
                    if (InstanceSource is LocalInstanceSource instance_source_local2) instance_source_local2.ConnectingProfileList.CollectionChanged += (object sender, NotifyCollectionChangedEventArgs e) => _forget_selected_profile.RaiseCanExecuteChanged();
                    Parent.Sessions.CollectionChanged += (object sender, NotifyCollectionChangedEventArgs e) => _forget_selected_profile.RaiseCanExecuteChanged();
                }

                return _forget_selected_profile;
            }
        }
        private DelegateCommand _forget_selected_profile;

        #endregion

        #region Constructors

        /// <summary>
        /// Constructs a panel
        /// </summary>
        /// <param name="parent">The page parent</param>
        /// <param name="instance_source_type">Instance source type</param>
        public ConnectingSelectPanel(ConnectWizard parent, InstanceSourceType instance_source_type)
        {
            Parent = parent;
            InstanceSourceType = instance_source_type;

            PropertyChanged += (object sender, PropertyChangedEventArgs e) =>
            {
                if (e.PropertyName == nameof(SelectedInstance))
                    RaisePropertyChanged(nameof(ForgetSelectedInstanceLabel));
                else if (e.PropertyName == nameof(SelectedProfile))
                    RaisePropertyChanged(nameof(ForgetSelectedProfileLabel));
            };
        }

        #endregion

        #region Methods

        /// <summary>
        /// Removes given instance from history
        /// </summary>
        /// <param name="instance">Instance</param>
        private void ForgetInstance(Instance instance)
        {
            if (InstanceSource is LocalInstanceSource instance_source_local)
            {
                // Remove all instance profiles from history.
                for (var i = instance_source_local.ConnectingProfileList.Count; i-- > 0;)
                    if (instance_source_local.ConnectingProfileList[i].Instance.Equals(instance))
                        instance_source_local.ConnectingProfileList.RemoveAt(i);
            }

            // Remove the instance from history.
            InstanceSource.ConnectingInstanceList.Remove(instance);

            // Reset connecting instance.
            if (InstanceSource.ConnectingInstance != null && InstanceSource.ConnectingInstance.Equals(instance))
            {
                InstanceSource.ConnectingInstance = InstanceSource.ConnectingInstanceList.FirstOrDefault();
                if (InstanceSource is LocalInstanceSource)
                    InstanceSource.AuthenticatingInstance = InstanceSource.ConnectingInstance;
            }

            // Test if it is safe to remove authorization token and certificate.
            if (InstanceSource.AuthenticatingInstance != null && InstanceSource.AuthenticatingInstance.Equals(instance))
                return;
            for (var source_index = (int)InstanceSourceType._start; source_index < (int)InstanceSourceType._end; source_index++)
                if (Parent.InstanceSources[source_index].AuthenticatingInstance != null && Parent.InstanceSources[source_index].AuthenticatingInstance.Equals(instance)/* ||
                    Parent.InstanceSources[source_index].ConnectingInstanceList.FirstOrDefault(inst => inst.Equals(instance)) != null*/)
                    return;

            instance.Forget();
        }

        #endregion
    }
}