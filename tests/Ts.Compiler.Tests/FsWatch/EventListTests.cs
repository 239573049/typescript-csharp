// Ported from tsgo internal/fswatch/eventlist_test.go — unit tests for
// EventList coalescing and drain semantics. This is pure logic (no OS), so it
// is a faithful 1:1 port and runs deterministically on every platform.

using System;
using System.Collections.Generic;
using Ts.FsWatch;
using Xunit;

namespace Ts.FsWatch.Tests;

public class EventListTests
{
    [Fact]
    public void CreateThenDelete()
    {
        var el = new EventList();
        el.Create("a");
        el.Remove("a");
        Assert.Equal(1, el.Size());
        Assert.Empty(el.GetEvents()); // getEvents should drop create+delete
    }

    [Fact]
    public void DeleteThenCreate()
    {
        var el = new EventList();
        el.Remove("a");
        el.Create("a");
        List<Event> got = el.GetEvents();
        Assert.Single(got);
        // "Assume update event when rapidly removed and created".
        Assert.Equal(EventKind.EventUpdate, got[0].Kind);
    }

    [Fact]
    public void CreateDeleteCreate()
    {
        var el = new EventList();
        el.Create("a");
        el.Remove("a");
        el.Create("a");
        List<Event> got = el.GetEvents();
        Assert.Single(got);
        Assert.Equal(EventKind.EventUpdate, got[0].Kind);
    }

    [Fact]
    public void ErrorIsLatchedAndCleared()
    {
        var el = new EventList();
        Assert.False(el.HasError());
        Assert.Null(el.GetError());

        el.SetError(new Exception("first"));
        el.SetError(new Exception("second")); // only first wins
        Assert.True(el.HasError());
        Exception? got = el.GetError();
        Assert.NotNull(got);
        Assert.Equal("first", got!.Message);

        el.Clear();
        Assert.False(el.HasError());
        Assert.Null(el.GetError());
    }

    [Fact]
    public void DrainIsAtomic()
    {
        var el = new EventList();
        el.Create("a");
        el.Update("b");
        el.SetError(new Exception("oops"));

        (List<Event> events, Exception? err) = el.Drain();
        Assert.NotNull(err);
        Assert.Equal(2, events.Count);

        (List<Event> events2, Exception? err2) = el.Drain();
        Assert.Null(err2);
        Assert.Empty(events2);
    }

    [Fact]
    public void DrainReturnsErrorWithEvents()
    {
        var el = new EventList();
        el.Create("file.txt");
        el.SetError(new Exception("overflow"));

        (List<Event> events, Exception? err) = el.Drain();
        Assert.NotNull(err);
        Assert.Single(events);
    }

    [Fact]
    public void DrainForSequences()
    {
        var el = new EventList();
        el.Create("file.txt");
        ulong startAfterCreate = el.Sequence();
        el.Remove("file.txt");

        (List<Event>[] eventsByCallback, Exception? err) = el.DrainForSequences(new ulong[] { 0, startAfterCreate });
        Assert.Null(err);
        // create+delete should cancel for the original callback.
        Assert.Empty(eventsByCallback[0]);
        // delete visible for the later callback.
        Assert.Single(eventsByCallback[1]);
        Event got = eventsByCallback[1][0];
        Assert.Equal(EventKind.EventDelete, got.Kind);
        Assert.Equal("file.txt", got.Path);
    }

    [Fact]
    public void EventKindStringValues()
    {
        Assert.Equal("update", EventKind.EventUpdate.StringValue());
        Assert.Equal("delete", EventKind.EventDelete.StringValue());
        Assert.Equal("unknown", ((EventKind)99).StringValue());
    }
}
