﻿using EventKit;
using Microsoft.Maui.ApplicationModel;

namespace Plugin.Maui.CalendarStore;

partial class CalendarStoreImplementation : ICalendarStore
{
	static EKEventStore? eventStore;

	static EKEventStore EventStore =>
		eventStore ??= new EKEventStore();

	/// <inheritdoc/>
	public async Task<IEnumerable<Calendar>> GetCalendars()
	{
		await Permissions.RequestAsync<Permissions.CalendarRead>();

		var calendars = EventStore.GetCalendars(EKEntityType.Event);

		return ToCalendars(calendars).ToList();
	}

	/// <inheritdoc/>
	public async Task<Calendar> GetCalendar(string calendarId)
	{
		ArgumentException.ThrowIfNullOrEmpty(calendarId);

		await Permissions.RequestAsync<Permissions.CalendarRead>();

		var calendars = EventStore.GetCalendars(EKEntityType.Event);

		var calendar = calendars.FirstOrDefault(c => c.CalendarIdentifier == calendarId);

		return calendar is null ? throw CalendarStore.InvalidCalendar(calendarId) : ToCalendar(calendar);
	}

	/// <inheritdoc/>
	public async Task<IEnumerable<CalendarEvent>> GetEvents(string? calendarId = null, DateTimeOffset? startDate = null, DateTimeOffset? endDate = null)
	{
		await Permissions.RequestAsync<Permissions.CalendarRead>();

		var startDateToConvert = startDate ?? DateTimeOffset.Now.Add(CalendarStore.defaultStartTimeFromNow);
		var endDateToConvert = endDate ?? startDateToConvert.Add(CalendarStore.defaultEndTimeFromStartTime); // NOTE: 4 years is the maximum period that a iOS calendar events can search

		var sDate = NSDate.FromTimeIntervalSince1970(TimeSpan.FromMilliseconds(startDateToConvert.ToUnixTimeMilliseconds()).TotalSeconds);
		var eDate = NSDate.FromTimeIntervalSince1970(TimeSpan.FromMilliseconds(endDateToConvert.ToUnixTimeMilliseconds()).TotalSeconds);

		var calendars = EventStore.GetCalendars(EKEntityType.Event);

		if (!string.IsNullOrEmpty(calendarId))
		{
			var calendar = calendars.FirstOrDefault(c => c.CalendarIdentifier == calendarId);

			if (calendar is null)
			{
				throw CalendarStore.InvalidCalendar(calendarId);
			}

			calendars = new[] { calendar };
		}

		var query = EventStore.PredicateForEvents(sDate, eDate, calendars);
		var events = EventStore.EventsMatching(query);

		return ToEvents(events).OrderBy(e => e.StartDate).ToList();
	}

	/// <inheritdoc/>
	public Task<CalendarEvent> GetEvent(string eventId)
	{
		if (EventStore.GetCalendarItem(eventId) is not EKEvent calendarEvent)
		{
			throw CalendarStore.InvalidEvent(eventId);
		}

		return Task.FromResult(ToEvent(calendarEvent));
	}

	/// <inheritdoc/>
	public Task CreateEvent(string calendarId, string title, string description, DateTimeOffset startDateTime, DateTimeOffset endDateTime, bool isAllDay = false)
	{
		return InternalSaveEvent(calendarId, title, description, startDateTime, endDateTime, isAllDay);
	}

	/// <inheritdoc/>
	public Task CreateEvent(CalendarEvent calendarEvent)
	{
		return CreateEvent(calendarEvent.CalendarId, calendarEvent.Title, calendarEvent.Description,
			calendarEvent.StartDate, calendarEvent.EndDate, calendarEvent.AllDay);
	}

	/// <inheritdoc/>
	public Task CreateAllDayEvent(string calendarId, string title, string description, DateTimeOffset startDate, DateTimeOffset endDate)
	{
		return InternalSaveEvent(calendarId, title, description, startDate, endDate, true);
	}

	static async Task InternalSaveEvent(string calendarId, string title, string description,
		DateTimeOffset startDateTime, DateTimeOffset endDateTime, bool isAllDayEvent = false)
	{
		var permissionResult = await Permissions.RequestAsync<Permissions.CalendarWrite>();

		if (permissionResult != PermissionStatus.Granted)
		{
			throw new PermissionException("Permission for writing to calendar store is not granted.");
		}

		var platformCalendar = EventStore.GetCalendar(calendarId) ?? throw CalendarStore.InvalidCalendar(calendarId);

		if (!platformCalendar.AllowsContentModifications)
		{
			throw new Exception($"Selected calendar (id: {calendarId}) is read-only.");
		}

		var accessRequest = await EventStore.RequestAccessAsync(EKEntityType.Event);

		// An error occurred on the platform level
		if (accessRequest.Item2 is not null)
		{
			throw new Exception($"Error occurred while accessing platform calendar store: " +
				$"{accessRequest.Item2.Description}");
		}

		// Permission was not granted
		if (!accessRequest.Item1)
		{
			throw new Exception("Could not access platform calendar store.");
		}

		var eventToSave = EKEvent.FromStore(EventStore);
		eventToSave.Calendar = platformCalendar;
		eventToSave.Title = title;
		eventToSave.Notes = description;
		eventToSave.StartDate = (NSDate)startDateTime.LocalDateTime;
		eventToSave.EndDate = (NSDate)endDateTime.LocalDateTime;
		eventToSave.AllDay = isAllDayEvent;
		
		var saveResult = EventStore.SaveEvent(eventToSave, EKSpan.ThisEvent, true, out var error);

		if (!saveResult || error is not null)
		{
			if (error is not null)
			{
				throw new Exception($"Error occurred while saving event: " +
					$"{error.LocalizedDescription}");
			}

			throw new Exception("Saving the event was unsuccessful.");
		}
	}

	static IEnumerable<Calendar> ToCalendars(IEnumerable<EKCalendar> native)
	{
		foreach (var calendar in native)
		{
			yield return ToCalendar(calendar);
		}
	}

	static Calendar ToCalendar(EKCalendar calendar) =>
		new(calendar.CalendarIdentifier, calendar.Title);

	static IEnumerable<CalendarEvent> ToEvents(IEnumerable<EKEvent> native)
	{
		foreach (var e in native)
		{
			yield return ToEvent(e);
		}
	}

	static CalendarEvent ToEvent(EKEvent platform) =>
		new(platform.CalendarItemIdentifier,
			platform.Calendar.CalendarIdentifier,
			platform.Title)
		{
			Description = platform.Notes,
			Location = platform.Location,
			AllDay = platform.AllDay,
			StartDate = ToDateTimeOffsetWithTimezone(platform.StartDate, platform.TimeZone),
			EndDate = ToDateTimeOffsetWithTimezone(platform.EndDate, platform.TimeZone),
			Attendees = platform.Attendees != null
				? ToAttendees(platform.Attendees).ToList()
				: new List<CalendarEventAttendee>()
		};

	static IEnumerable<CalendarEventAttendee> ToAttendees(IEnumerable<EKParticipant> inviteList)
	{
		foreach (var attendee in inviteList)
		{
			// There is no obvious way to get the attendees email address on iOS?
			yield return new(attendee.Name, attendee.Name);
		}
	}

	static DateTimeOffset ToDateTimeOffsetWithTimezone(NSDate platformDate, NSTimeZone? timezone)
	{
		var timezoneToApply = NSTimeZone.DefaultTimeZone;

		if (timezone is not null)
		{
			timezoneToApply = timezone;
		}

		return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
			(DateTime)platformDate, timezoneToApply.Name);
	}
}