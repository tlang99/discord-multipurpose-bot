using Discord;
using Discord.Interactions;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Services.Chat;
using ModBot.Services.Storage;

namespace ModBot.Modules.Chat;

/// <summary>Chatbot settings for server admins. The chat itself (answering mentions) is in ChatHandler.</summary>
[Group("chat", "Configure the AI chatbot.")]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.ManageGuild)]
[DefaultMemberPermissions(GuildPermission.ManageGuild)]
public class ChatModule : InteractionModuleBase<SocketInteractionContext>
{
    // Repeated from the outer group so the check doesn't depend on it carrying over, and so it also covers the modal
    // submission, which is its own interaction.
    [Group("personality", "Change how the chatbot talks.")]
    [RequireContext(ContextType.Guild)]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public class PersonalityModule : InteractionModuleBase<SocketInteractionContext>
    {
        private const string ModalId = "chat-personality-modal";
        private const string InputId = "personality";

        private readonly IChatSettingsStore _store;
        private readonly ChatPersonality _personality;

        public PersonalityModule(IChatSettingsStore store, ChatPersonality personality)
        {
            _store = store;
            _personality = personality;
        }

        [SlashCommand("set", "Edit the chatbot's personality in a pop-up, starting from the current one.")]
        public async Task SetAsync()
        {
            var current = await CurrentAsync();
            var modal = new ModalBuilder()
                .WithTitle("Chatbot personality")
                .WithCustomId(ModalId)
                .AddTextInput("How should the bot talk and behave?", InputId, TextInputStyle.Paragraph,
                    placeholder: "e.g. A sarcastic veteran gamer who roasts bad plays but always helps in the end.",
                    maxLength: ChatOptions.MaxPersonalityLength, required: false,
                    value: current.Text.Length > ChatOptions.MaxPersonalityLength
                        ? current.Text[..ChatOptions.MaxPersonalityLength]
                        : current.Text)
                .Build();

            await RespondWithModalAsync(modal);
        }

        // ignoreGroupNames: the modal's ID is a plain string, not prefixed with "chat personality". Discord.Net requires
        // a modal handler to take its values as an IModal parameter; it refuses to start otherwise.
        [ModalInteraction(ModalId, ignoreGroupNames: true)]
        public async Task OnSubmittedAsync(PersonalityModal modal)
        {
            var text = modal.Personality?.Trim() ?? string.Empty;

            if (text.Length == 0)
            {
                await _store.SetPersonalityAsync(Context.Guild.Id, null);
                await RespondAsync("Cleared the personality, so the chatbot is back to the default one.", ephemeral: true);
                return;
            }

            if (text.Length > ChatOptions.MaxPersonalityLength)
            {
                text = text[..ChatOptions.MaxPersonalityLength];
            }

            await _store.SetPersonalityAsync(Context.Guild.Id, text);
            await RespondAsync(embed: PersonalityEmbed("Personality updated", text,
                "Mention me to try it out. /chat personality reset goes back to the default."), ephemeral: true);
        }

        [SlashCommand("show", "Shows the chatbot's current personality.")]
        public async Task ShowAsync()
        {
            var current = await CurrentAsync();
            await RespondAsync(embed: PersonalityEmbed(
                current.IsCustom ? "This server's personality" : "Default personality",
                current.Text,
                current.IsCustom
                    ? "Set with /chat personality set. /chat personality reset goes back to the default."
                    : "Change it for this server with /chat personality set."), ephemeral: true);
        }

        [SlashCommand("reset", "Puts the chatbot back to its default personality.")]
        public async Task ResetAsync()
        {
            await _store.SetPersonalityAsync(Context.Guild.Id, null);
            await RespondAsync("The chatbot is back to its default personality.", ephemeral: true);
        }

        private Task<(string Text, bool IsCustom)> CurrentAsync() => _personality.GetWithSourceAsync(Context.Guild.Id);

        private static Embed PersonalityEmbed(string title, string text, string footer) => new EmbedBuilder()
            .WithColor(Colors.Bot)
            .WithTitle(title)
            .WithDescription(text)
            .WithFooter(footer)
            .Build();
    }

    /// <summary>The values submitted from the personality pop-up. The pop-up itself is built in
    /// <see cref="PersonalityModule.SetAsync"/> (so it can be pre-filled); this only reads it back, matching the text
    /// box by its custom ID.</summary>
    public class PersonalityModal : IModal
    {
        public string Title => "Chatbot personality";

        [InputLabel("How should the bot talk and behave?")]
        [ModalTextInput("personality", TextInputStyle.Paragraph, maxLength: ChatOptions.MaxPersonalityLength)]
        public string? Personality { get; set; }
    }
}
