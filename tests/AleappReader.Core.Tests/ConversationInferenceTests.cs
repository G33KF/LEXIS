namespace AleappReader.Core.Tests;

public class ConversationInferenceTests
{
    static ColumnInfo C(string display, string? type = null) => new(display.ToLowerInvariant().Replace(' ', '_'), display, type);

    [Fact]
    public void A_table_of_messages_that_declares_no_conversation_view_is_read_as_one_from_its_column_names()
    {
        // Telegram - Messages
        var v = ConversationView.Infer([C("Timestamp", "datetime"), C("Dialog ID"), C("Chat"), C("Direction"), C("Sender ID"), C("Sender"),
                                        C("Message"), C("Recorded Media Path"), C("Media File", "media"), C("Read State"), C("Message ID")])!;
        Assert.Equal(("dialog_id", "chat", "message", "direction", "timestamp", "sender", "media_file"),
            (v.Discriminator, v.LabelColumn, v.Text, v.Direction, v.Time, v.Sender, v.Media));
        Assert.True(v.IsSent("Outgoing"));
        Assert.False(v.IsSent("Incoming"));
        // a declared value is matched as it is
        Assert.True((v with { SentValue = "1" }).IsSent(" 1 "));

        // without the chat a message belongs to, or a date, or a text: not a conversation
        Assert.Null(ConversationView.Infer([C("Timestamp", "datetime"), C("Message"), C("Sender")]));
        Assert.Null(ConversationView.Infer([C("Chat"), C("Message")]));
        Assert.Null(ConversationView.Infer([C("Timestamp", "datetime"), C("Chat"), C("Message ID")]));
    }
}
