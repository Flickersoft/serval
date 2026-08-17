using Serval.Server.Alerts;

namespace Serval.Server.Tests;

/// <summary>
/// The sentence an alert is announced with.
///
/// Worth testing because it is stored: a title is written once when the alert is raised and read
/// forever after, so a change here does not correct the rows already in the queue — it splits them
/// into two eras. It is also the text a push notification carries, which makes it the one string in
/// Serval a person reads before they read anything else.
/// </summary>
public class AlertTitleTests
{
    [Fact]
    public void An_object_alert_names_the_class_and_the_camera()
    {
        Assert.Equal("Person at Front door", AlertTitle.ForObject("person", "Front door"));
    }

    [Fact]
    public void A_lowercase_class_is_capitalised_to_start_a_sentence()
    {
        Assert.Equal("Car at Driveway", AlertTitle.ForObject("car", "Driveway"));
    }

    [Fact]
    public void A_class_that_already_capitalises_keeps_what_the_model_sent()
    {
        // Only the first letter is touched, so a model whose classes are acronyms or proper nouns is
        // not quietly rewritten.
        Assert.Equal("ANPR at Gate", AlertTitle.ForObject("ANPR", "Gate"));
    }

    [Fact]
    public void A_sound_alert_says_it_was_heard()
    {
        Assert.Equal("Glass heard at Back yard", AlertTitle.ForSound("Glass", "Back yard"));
    }

    [Fact]
    public void A_sound_label_that_lists_its_synonyms_uses_only_the_first()
    {
        // AudioSet names several classes as a list — useful in a taxonomy, a stutter in a
        // notification.
        Assert.Equal(
            "Smoke detector heard at Kitchen",
            AlertTitle.ForSound("Smoke detector, smoke alarm", "Kitchen"));

        Assert.Equal("Gunshot heard at Street", AlertTitle.ForSound("Gunshot, gunfire", "Street"));
    }

    [Fact]
    public void An_empty_label_still_produces_a_sentence()
    {
        // Nothing should ever send one, but a title is what the queue draws — an alert with a blank
        // row is worse than one with a vague row.
        Assert.Equal("Detection at Front door", AlertTitle.ForObject("", "Front door"));
    }
}
