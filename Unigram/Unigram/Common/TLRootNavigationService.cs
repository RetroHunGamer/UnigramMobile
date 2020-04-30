﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Template10.Services.NavigationService;
using Unigram.Controls;
using Unigram.Services;
using Unigram.ViewModels;
using Unigram.ViewModels.SignIn;
using Unigram.Views;
using Unigram.Views.SignIn;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unigram.Common
{
    public class TLRootNavigationService : NavigationService, IHandle<UpdateAuthorizationState>
    {
        private readonly ILifetimeService _lifetimeService;
        private readonly ISessionService _sessionService;

        public TLRootNavigationService(ISessionService sessionService, Frame frame, int session, string id)
            : base(frame, session, id)
        {
            _lifetimeService = TLContainer.Current.Lifetime;
            _sessionService = sessionService;
        }

        public async void Handle(UpdateAuthorizationState update)
        {
            switch (update.AuthorizationState)
            {
                case AuthorizationStateReady ready:
                    Navigate(typeof(MainPage));
                    break;
                case AuthorizationStateWaitPhoneNumber waitPhoneNumber:
                case AuthorizationStateWaitOtherDeviceConfirmation waitOtherDeviceConfirmation:
                    if (_lifetimeService.Items.Count > 1)
                    {
                        if (Frame.Content is SignInPage page && page.DataContext is SignInViewModel viewModel)
                        {
                            await viewModel.OnNavigatedToAsync(null, NavigationMode.Refresh, null);
                        }
                        else
                        {
                            Navigate(typeof(SignInPage));
                        }

                        Frame.BackStack.Clear();
                        Frame.BackStack.Add(new PageStackEntry(typeof(BlankPage), null, null));
                    }
                    else
                    {
                        if (Frame.Content is SignInPage page && page.DataContext is SignInViewModel viewModel)
                        {
                            await viewModel.OnNavigatedToAsync(null, NavigationMode.Refresh, null);
                        }
                        else
                        {
                            Navigate(typeof(IntroPage));
                        }
                    }
                    break;
                case AuthorizationStateWaitCode waitCode:
                    Navigate(typeof(SignInSentCodePage));
                    break;
                case AuthorizationStateWaitRegistration waitRegistration:
                    Navigate(typeof(SignUpPage));
                    break;
                case AuthorizationStateWaitPassword waitPassword:
                    if (!string.IsNullOrEmpty(waitPassword.RecoveryEmailAddressPattern))
                    {
                        await TLMessageDialog.ShowAsync(string.Format(Strings.Resources.RestoreEmailSent, waitPassword.RecoveryEmailAddressPattern), Strings.Resources.AppName, Strings.Resources.OK);
                    }

                    Navigate(string.IsNullOrEmpty(waitPassword.RecoveryEmailAddressPattern) ? typeof(SignInPasswordPage) : typeof(SignInRecoveryPage));
                    break;
            }
        }
    }
}
