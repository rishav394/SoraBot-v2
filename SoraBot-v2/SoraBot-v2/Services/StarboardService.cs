﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SoraBot_v2.Data;
using SoraBot_v2.Data.Entities.SubEntities;

namespace SoraBot_v2.Services
{
    public class StarboardService
    {
        private readonly DiscordSocketClient _client;
        private Timer _timer;
        private List<StarMsgUpdateStruct> _toUpdate = new List<StarMsgUpdateStruct>();

        public StarboardService(DiscordSocketClient client)
        {
            _client = client;
        }

        public void Initialize()
        {
            Task.Factory.StartNew(() => { _timer = new Timer(UpdateStarCounts, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20)); });
        }

        private async void UpdateStarCounts(Object objectInfo)
        {
            try
            {
                //return if there is nothing to update
                if(_toUpdate.Count == 0)
                    return;
                //otherwise lets update all of the entires
                List<StarMsgUpdateStruct> temp = new List<StarMsgUpdateStruct>(_toUpdate);
                using (SoraContext soraContext = new SoraContext())
                {
                    foreach (var updateStruct in temp)
                    {
                        var guild = _client.GetGuild(updateStruct.GuildId);
                        if(guild == null){
                            //he was kicked after star or guild moved shards. 
                            //remove and update entry
                            _toUpdate.Remove(updateStruct);
                            continue;
                        }
                        var guildDb = Utility.GetOrCreateGuild(updateStruct.GuildId, soraContext);
                        //check if starchannel still exists!
                        var starChannel = guild.GetTextChannel(guildDb.StarChannelId);
                        if (starChannel == null)
                        {
                            //channel doesnt exists anymore
                            //remove update entry
                            _toUpdate.Remove(updateStruct);
                            continue;
                        }
                        //check perms
                        if (await Utility.CheckReadWritePerms(guild, starChannel, false) == false)
                        {
                            //remove update entry
                            _toUpdate.Remove(updateStruct);
                            continue;
                        }
                        //Get Message
                        var starMsg = await CacheService.GetUserMessage(updateStruct.PostedMsgId);
                        //if Msg wasn't cached then there wasn't an update to the count.
                        if (starMsg == null)
                        {
                            //remove update entry
                            _toUpdate.Remove(updateStruct);
                            continue;
                        }
                        int amount;
                        if (!int.TryParse(
                            starMsg.Content.Substring(0, starMsg.Content.IndexOf(" ", StringComparison.Ordinal))
                                .Replace("**", ""), out amount))
                        {
                            //parse failed for some reason
                            //remove update entry
                            _toUpdate.Remove(updateStruct);
                            continue;
                        }
                        //get starmessage
                        var starMessage = guildDb.StarMessages.FirstOrDefault(x => x.PostedMsgId == starMsg.Id);
                        if (starMessage == null)
                        {
                            //failed to get starmessage
                            //remove update entry
                            _toUpdate.Remove(updateStruct);
                            continue;
                        }
                        if (amount == starMessage.StarCount)
                        {
                            //remove update entry
                            _toUpdate.Remove(updateStruct);
                            continue;
                        }
                        try
                        {
                            await starMsg.ModifyAsync(x =>
                            {
                                x.Content = $"**{starMessage.StarCount}**{starMsg.Content.Substring(starMsg.Content.IndexOf(" ", StringComparison.Ordinal))}";
                            });
                        }
                        catch (Discord.Net.HttpException)
                        {
                            //if this was cought the cached message isnt valid anymore so remove it. 
                            CacheService.RemoveUserMessage(starMsg.Id);
                            //remove update entry
                            _toUpdate.Remove(updateStruct);
                            continue;
                        }
                        //remove update entry
                        _toUpdate.Remove(updateStruct);
                        await CacheService.SetDiscordUserMessage(starChannel, starMessage.PostedMsgId,
                            TimeSpan.FromHours(1));
                    }
                }
            }
            catch (Exception e)
            {
                await SentryService.SendMessage(e.ToString());
            }
        }

        public async Task ClientOnReactionAdded(Cacheable<IUserMessage, ulong> cacheable, ISocketMessageChannel socketMessageChannel, SocketReaction reaction)
        {
            try
            {
                //Reaction doesn't match a star
                if (!reaction.Emote.Name.Equals("⭐"))
                    return;
                //get Message
                var msg = await cacheable.GetOrDownloadAsync();
                //Dont do anything if the msg originates from a bot
                if (msg.Author.IsBot)
                    return;
                //Reaction was a star
                using (SoraContext soraContext = new SoraContext())
                {
                    var guild = ((SocketGuildChannel)socketMessageChannel).Guild;
                    var guildDb = Utility.GetOrCreateGuild(guild.Id, soraContext);
                    //Either the starboard wasn't set up or the channel doesnt exist anymore.
                    if (guildDb.StarChannelId == 0)
                        return;
                    var starChannel = guild.GetTextChannel(guildDb.StarChannelId);
                    if (starChannel == null)
                    {
                        //guildDb.StarChannelId = 0; //Reset the channelID to 0 so in the future we dont have to save anything anymore :D
                        //await soraContext.SaveChangesAsync(); //TODO TEMPORARILY DISABLED DUE TO SOME ERROR
                        return;
                    }
                    //Check if reaction is from author
                    if (msg.Author.Id == reaction.UserId)
                        return;
                    //check if it was added once before and if it was added too many times!
                    var starMsg = guildDb.StarMessages.FirstOrDefault(x => x.MessageId == msg.Id);
                    if (starMsg != null && starMsg.HitZeroCount >= 3)
                    {
                        return;
                    }
                    //if it was null create a new one otherwise keep the old one
                    bool wasNull = false;
                    if (starMsg == null)
                    {
                        starMsg = new StarMessage()
                        {
                            GuildForeignId = guild.Id,
                            HitZeroCount = 0,
                            MessageId = msg.Id,
                            StarCount = 0,
                            IsPosted = false
                        };
                        wasNull = true;
                    }

                    //Add star
                    starMsg.StarCount++;
                    //Check if its enough to post
                    if (starMsg.StarCount >= guildDb.StarMinimum && !starMsg.IsPosted)
                    {
                        //POST
                        starMsg.PostedMsgId = await PostStarMessage(starChannel, msg);
                        if (starMsg.PostedMsgId == 0)
                        {
                            try
                            {
                                await socketMessageChannel.SendMessageAsync("", embed: Utility.ResultFeedback(
                                    Utility.RedFailiureEmbed, Utility.SuccessLevelEmoji[2], "Something failed. Can't add msg to starboard. Serenity#0783 has been notified"));
                            }
                            catch (Exception e)
                            {
                                await SentryService.SendMessage("EVEN FAILED WITH ERROR MESSAGEEEEEEEEEEEEE :C\n" + e);
                                return;
                            }

                            return;
                        }
                        starMsg.IsPosted = true;
                    }

                    //save changes made
                    if (wasNull)
                        guildDb.StarMessages.Add(starMsg);
                    await soraContext.SaveChangesAsync();
                    //if it wasnt null and posted add to update
                    if (!wasNull && starMsg.IsPosted)
                    {
                        //add it to the starmsg update cache so we can update the count!
                        _toUpdate.Add(new StarMsgUpdateStruct()
                        {
                            GuildId = guild.Id,
                            PostedMsgId = starMsg.PostedMsgId
                        });
                    }
                    //check if starpostedmsg == 0
                    if (starMsg.PostedMsgId != 0)
                    {
                        await CacheService.SetDiscordUserMessage(starChannel, starMsg.PostedMsgId, TimeSpan.FromHours(1));
                    }
                }
            }
            catch (Exception e)
            {
                await SentryService.SendMessage(e.ToString());
            }
        }


        public async Task ClientOnReactionRemoved(Cacheable<IUserMessage, ulong> cacheable, ISocketMessageChannel socketMessageChannel, SocketReaction reaction)
        {
            //Reaction doesn't match a star
            if (!reaction.Emote.Name.Equals("⭐"))
                return;
            //get Message
            var msg = await cacheable.GetOrDownloadAsync();
            using (SoraContext soraContext = new SoraContext())
            {
                var guild = ((SocketGuildChannel)socketMessageChannel).Guild;
                var guildDb = Utility.GetOrCreateGuild(guild.Id, soraContext);
                //Either the starboard wasn't set up or the channel doesnt exist anymore.
                if (guildDb.StarChannelId == 0)
                    return;
                var starChannel = guild.GetTextChannel(guildDb.StarChannelId);
                if (starChannel == null)
                {
                    //guildDb.StarChannelId =0; //Reset the channelID to 0 so in the future we dont have to save anything anymore :D
                    //await soraContext.SaveChangesAsync(); //TODO TEMPORARILY DISABLED AS IT BREAKS THE FUCKING STARBOARD
                    return;
                }
                //Check if reaction is from author
                if (msg.Author.Id == reaction.UserId)
                    return;
                //check if the starmessage exists in the DB
                var starMsg = guildDb.StarMessages.FirstOrDefault(x => x.MessageId == msg.Id);
                if (starMsg == null)
                    return;
                //Reduce starcount
                starMsg.StarCount--;
                //starcount hit 0 or went below magically :thonk: => DELETE
                if (starMsg.StarCount < 1)
                {
                    starMsg.HitZeroCount++; //If it reaches 3 or beyond it wont get added anymore.

                    var postedStarMsg = await starChannel.GetMessageAsync(starMsg.PostedMsgId);
                    //delete the msg if not null otherwise proceed as if we deleted it :P
                    if (postedStarMsg != null)
                        await postedStarMsg.DeleteAsync();
                    //make sure to reset is posted so it can get posted again
                    starMsg.IsPosted = false;
                }
                else
                {
                    //add it to the starmsg update cache so we can update the count!
                    _toUpdate.Add(new StarMsgUpdateStruct()
                    {
                        GuildId = guild.Id,
                        PostedMsgId = starMsg.PostedMsgId
                    });
                }
                await soraContext.SaveChangesAsync();
                //cache to update timeout.
                await CacheService.SetDiscordUserMessage(starChannel, starMsg.PostedMsgId, TimeSpan.FromHours(1));
            }
        }

        private async Task<ulong> PostStarMessage(SocketTextChannel starChannel, IUserMessage msg)
        {

            string attachmentUrls = "";
            bool attachMent = false;
            bool picAttachment = false;
            string picAttach = "";

            if (msg.Attachments.Count > 0)
            {
                attachMent = true;
                if (msg.Attachments.Count == 1)
                {
                    var url = msg.Attachments.ToArray()[0].Url;
                    if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                    {
                        attachMent = false;
                        picAttachment = true;
                        picAttach = url;
                    }
                    else
                    {
                        attachmentUrls = url;
                    }
                }
                else
                {
                    foreach (var messageAttachment in msg.Attachments)
                    {
                        attachmentUrls += $"{messageAttachment.Url} \n";
                    }
                }
            }
            string messageContent = msg.Content ?? "";
            //CHECK FOR 1 IMAGE WITHIN THE VALUE
            if (!attachMent && !picAttachment)
            {
                var mc = Regex.Matches(messageContent, @"(https://[^ \s]+|http://[^ \s]+)([\s]|$)");
                if (mc.Count == 1)
                {
                    var link = mc[0].Value;
                    if (link.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || link.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || link.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                    {
                        picAttachment = true;
                        picAttach = link;
                        messageContent = messageContent.Remove(messageContent.IndexOf(link, StringComparison.Ordinal), link.Length);
                    }
                }
            }
            //Finally ADD
            var eb = new EmbedBuilder()
            {
                Color = Utility.PurpleEmbed,
                Author = new EmbedAuthorBuilder()
                {
                    IconUrl = msg.Author.GetAvatarUrl() ?? Utility.StandardDiscordAvatar,
                    Name = Utility.GiveUsernameDiscrimComb(msg.Author as SocketUser)
                },
                Timestamp = DateTime.Now,
                Description = (attachMent ? $"{messageContent}\n{attachmentUrls}" : messageContent)
            };
            if (picAttachment)
            {
                eb.ImageUrl = picAttach;
            }
            try
            {
                var postedMsg = await starChannel.SendMessageAsync($"**1** ⭐ in <#{msg.Channel.Id}> \n", embed: eb);
                return postedMsg.Id;
            }
            catch (Exception e)
            {
                await SentryService.SendMessage("STARBOARD ERROR\n" + e);
            }
            return 0;
        }

        struct StarMsgUpdateStruct
        {
            public ulong GuildId { get; set; }
            public ulong PostedMsgId { get; set; }
        }
    }
}