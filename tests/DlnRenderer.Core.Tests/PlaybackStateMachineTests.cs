using DlnRenderer.Core.Models;
using DlnRenderer.Core.State;

namespace DlnRenderer.Core.Tests;

public class PlaybackStateMachineTests
{
    [Theory]
    [InlineData(PlaybackState.Stopped, PlaybackState.Transitioning)]
    [InlineData(PlaybackState.Transitioning, PlaybackState.Playing)]
    [InlineData(PlaybackState.Transitioning, PlaybackState.Paused)]
    [InlineData(PlaybackState.Playing, PlaybackState.Paused)]
    [InlineData(PlaybackState.Paused, PlaybackState.Playing)]
    [InlineData(PlaybackState.Playing, PlaybackState.Buffering)]
    [InlineData(PlaybackState.Buffering, PlaybackState.Playing)]
    [InlineData(PlaybackState.Playing, PlaybackState.Stopped)]
    [InlineData(PlaybackState.Paused, PlaybackState.Stopped)]
    [InlineData(PlaybackState.Buffering, PlaybackState.Stopped)]
    [InlineData(PlaybackState.Playing, PlaybackState.Error)]
    [InlineData(PlaybackState.Error, PlaybackState.Stopped)]
    [InlineData(PlaybackState.Error, PlaybackState.Transitioning)]
    public void Legal_transitions_are_accepted(PlaybackState from, PlaybackState to)
    {
        Assert.True(PlaybackStateRules.CanTransition(from, to));
    }

    [Theory]
    [InlineData(PlaybackState.Stopped, PlaybackState.Playing)]
    [InlineData(PlaybackState.Stopped, PlaybackState.Paused)]
    [InlineData(PlaybackState.Error, PlaybackState.Playing)]
    [InlineData(PlaybackState.Error, PlaybackState.Paused)]
    [InlineData(PlaybackState.Error, PlaybackState.Buffering)]
    public void Illegal_transitions_are_rejected(PlaybackState from, PlaybackState to)
    {
        Assert.NotEqual(from, to);
        Assert.False(PlaybackStateRules.CanTransition(from, to));
    }

    [Fact]
    public void Repeating_the_current_state_is_allowed_and_raises_no_event()
    {
        var machine = new PlaybackStateMachine();
        var raised = 0;
        machine.StateChanged += (_, _) => raised++;

        Assert.True(machine.TryTransitionTo(PlaybackState.Stopped));

        Assert.Equal(PlaybackState.Stopped, machine.Current);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Transition_raises_state_changed_with_from_and_to()
    {
        var machine = new PlaybackStateMachine();
        PlaybackStateChangedEventArgs? args = null;
        machine.StateChanged += (_, e) => args = e;

        machine.TransitionTo(PlaybackState.Transitioning);

        Assert.NotNull(args);
        Assert.Equal(PlaybackState.Stopped, args!.From);
        Assert.Equal(PlaybackState.Transitioning, args.To);
        Assert.Equal(PlaybackState.Transitioning, machine.Current);
    }

    [Fact]
    public void Transition_to_illegal_state_throws_and_keeps_current()
    {
        var machine = new PlaybackStateMachine();

        var ex = Assert.Throws<InvalidPlaybackTransitionException>(
            () => machine.TransitionTo(PlaybackState.Playing));

        Assert.Equal(PlaybackState.Stopped, ex.From);
        Assert.Equal(PlaybackState.Playing, ex.To);
        Assert.Equal(PlaybackState.Stopped, machine.Current);
        Assert.False(machine.TryTransitionTo(PlaybackState.Playing));
    }

    [Fact]
    public void Undefined_state_values_are_never_legal_targets()
    {
        var undefined = (PlaybackState)42;

        Assert.False(PlaybackStateRules.CanTransition(PlaybackState.Stopped, undefined));
        Assert.False(PlaybackStateRules.CanTransition(undefined, PlaybackState.Playing));
        Assert.False(PlaybackStateRules.CanTransition(undefined, undefined));
    }
}
