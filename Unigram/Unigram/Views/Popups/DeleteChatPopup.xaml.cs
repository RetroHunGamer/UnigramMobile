﻿using LinqToVisualTree;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Services;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

namespace Unigram.Views.Popups
{
    public sealed partial class DeleteChatPopup : TLContentDialog
    {
        public DeleteChatPopup(IProtoService protoService, Chat chat, ChatList chatList, bool clear, bool asOwner = false)
        {
            InitializeComponent();

            Photo.Source = PlaceholderHelper.GetChat(protoService, chat, 36);

            var position = chat.GetPosition(chatList);
            if (position?.Source is ChatSourcePublicServiceAnnouncement)
            {
                TitleDelete.Text = Strings.Resources.PsaHideChatAlertTitle;
                Subtitle.Text = Strings.Resources.PsaHideChatAlertText;
                CheckBox.Visibility = Visibility.Collapsed;

                PrimaryButtonText = Strings.Resources.PsaHide;
                SecondaryButtonText = Strings.Resources.Cancel;

                return;
            }

            var user = protoService.GetUser(chat);
            var basicGroup = protoService.GetBasicGroup(chat);
            var supergroup = protoService.GetSupergroup(chat);

            var deleteAll = user != null && chat.Type is ChatTypePrivate privata && privata.UserId != protoService.Options.MyId && chat.CanBeDeletedForAllUsers;
            if (deleteAll)
            {
                CheckBox.Visibility = Visibility.Visible;

                var name = user.FirstName;
                if (string.IsNullOrEmpty(name))
                {
                    name = user.LastName;
                }

                if (clear)
                {
                    CheckBox.Content = string.Format(Strings.Resources.ClearHistoryOptionAlso, name);
                }
                else
                {
                    CheckBox.Content = string.Format(Strings.Resources.DeleteMessagesOptionAlso, name);
                }
            }

            if (clear)
            {
                if (user != null)
                {
                    if (chat.Type is ChatTypeSecret)
                    {
                        TextBlockHelper.SetMarkdown(Subtitle, string.Format(Strings.Resources.AreYouSureClearHistoryWithSecretUser, user.GetFullName()));
                    }
                    else if (user.Id == protoService.Options.MyId)
                    {
                        TextBlockHelper.SetMarkdown(Subtitle, Strings.Resources.AreYouSureClearHistorySavedMessages);
                    }
                    else
                    {
                        TextBlockHelper.SetMarkdown(Subtitle, string.Format(Strings.Resources.AreYouSureClearHistoryWithUser, user.GetFullName()));
                    }
                }
                else if (basicGroup != null)
                {
                    TextBlockHelper.SetMarkdown(Subtitle, string.Format(Strings.Resources.AreYouSureClearHistoryGroup));
                }
                else if (supergroup != null)
                {
                    if (supergroup.IsChannel)
                    {
                        TextBlockHelper.SetMarkdown(Subtitle, string.Format(Strings.Resources.AreYouSureClearHistoryChannel));
                    }
                    else if (string.IsNullOrEmpty(supergroup.Username))
                    {
                        TextBlockHelper.SetMarkdown(Subtitle, string.Format(Strings.Resources.AreYouSureClearHistoryWithChat, chat.Title));
                    }
                    else
                    {
                        TextBlockHelper.SetMarkdown(Subtitle, string.Format(Strings.Resources.AreYouSureClearHistoryGroup));
                    }
                }
            }
            else if (user != null)
            {
                if (chat.Type is ChatTypeSecret)
                {
                    TextBlockHelper.SetMarkdown(Subtitle, string.Format(Strings.Resources.AreYouSureDeleteThisChatWithSecretUser, user.GetFullName()));
                }
                else if (user.Id == protoService.Options.MyId)
                {
                    TextBlockHelper.SetMarkdown(Subtitle, Strings.Resources.AreYouSureDeleteThisChatSavedMessages);
                }
                else
                {
                    TextBlockHelper.SetMarkdown(Subtitle, string.Format(Strings.Resources.AreYouSureDeleteThisChatWithUser, user.GetFullName()));
                }

                if (user.Type is UserTypeBot)
                {
                    CheckBox.Visibility = Visibility.Visible;
                    CheckBox.Content = Strings.Resources.BotStop;
                }
            }
            else if (basicGroup != null)
            {
                TextBlockHelper.SetMarkdown(Subtitle, string.Format(Strings.Resources.AreYouSureDeleteAndExitName, chat.Title));
            }
            else if (supergroup != null)
            {
                if (asOwner)
                {
                    if (supergroup.IsChannel)
                    {
                        Subtitle.Text = Strings.Resources.ChannelDeleteAlert;
                    }
                    else
                    {
                        Subtitle.Text = Strings.Resources.MegaDeleteAlert;
                    }
                }
                else if (supergroup.IsChannel)
                {
                    TextBlockHelper.SetMarkdown(Subtitle, string.Format(Strings.Resources.ChannelLeaveAlertWithName, chat.Title));
                }
                else
                {
                    TextBlockHelper.SetMarkdown(Subtitle, string.Format(Strings.Resources.MegaLeaveAlertWithName, chat.Title));
                }
            }

            if (clear)
            {
                PrimaryButtonText = Strings.Resources.ClearHistory;
            }
            else if (user != null || basicGroup != null)
            {
                PrimaryButtonText = Strings.Resources.DeleteChatUser;
            }
            else if (supergroup != null)
            {
                if (supergroup.IsChannel)
                {
                    PrimaryButtonText = asOwner ? Strings.Resources.ChannelDeleteMenu : Strings.Resources.LeaveChannelMenu;
                }
                else
                {
                    PrimaryButtonText = asOwner ? Strings.Resources.DeleteMegaMenu : Strings.Resources.LeaveMegaMenu;
                }
            }

            TitleDelete.Text = PrimaryButtonText;
            SecondaryButtonText = Strings.Resources.Cancel;
        }

        public bool IsChecked => CheckBox.Visibility == Visibility.Visible && CheckBox.IsChecked == true;

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
        }

        private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
        }
    }
}
