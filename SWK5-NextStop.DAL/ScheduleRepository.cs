namespace SWK5_NextStop.DAL;

using SWK5_NextStop.Infrastructure;
using NextStop.Data;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Data.Common;

public class ScheduleRepository
{
    private readonly AdoTemplate _adoTemplate;

    public ScheduleRepository(IConnectionFactory connectionFactory)
    {
        _adoTemplate = new AdoTemplate(connectionFactory);
    }

    private Schedule MapRowToSchedule(DbDataReader reader) =>
        new Schedule
        {
            ScheduleId = reader.GetInt32(reader.GetOrdinal("schedule_id")),
            RouteId = reader.GetInt32(reader.GetOrdinal("route_id")),
            Date = reader.GetDateTime(reader.GetOrdinal("date")),
        };

    private RouteStopSchedule MapRowToRouteStopSchedule(DbDataReader reader) =>
        new RouteStopSchedule
        {
            ScheduleId = reader.GetInt32(reader.GetOrdinal("schedule_id")),
            StopId = reader.GetInt32(reader.GetOrdinal("stop_id")),
            SequenceNumber = reader.GetInt32(reader.GetOrdinal("sequence_number")),
            Time = TimeOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("time")))
        };

    public async Task<Schedule> CreateScheduleAsync(Schedule schedule)
    {
        string query = @"
            INSERT INTO schedule (route_id, date)
            VALUES (@routeId, @date)
            RETURNING schedule_id;";

        int generatedId = await _adoTemplate.ExecuteScalarAsync<int>(query,
            new QueryParameter("@routeId", schedule.RouteId),
            new QueryParameter("@date", schedule.Date));

        schedule.ScheduleId = generatedId;
        return schedule;
    }

    public async Task AddRouteStopScheduleAsync(RouteStopSchedule routeStopSchedule)
    {
        string query = @"
            INSERT INTO route_stop_schedule (schedule_id, stop_id, sequence_number, time)
            VALUES (@scheduleId, @stopId, @sequenceNumber, @time);";

        await _adoTemplate.ExecuteAsync(query,
            new QueryParameter("@scheduleId", routeStopSchedule.ScheduleId),
            new QueryParameter("@stopId", routeStopSchedule.StopId),
            new QueryParameter("@sequenceNumber", routeStopSchedule.SequenceNumber),
            new QueryParameter("@time", routeStopSchedule.Time));
    }

    public async Task<Schedule?> GetScheduleByIdAsync(int scheduleId)
    {
        string scheduleQuery = "SELECT * FROM schedule WHERE schedule_id = @scheduleId";
        string routeStopQuery = "SELECT * FROM route_stop_schedule WHERE schedule_id = @scheduleId";

        var schedule = await _adoTemplate.QuerySingleAsync(scheduleQuery, MapRowToSchedule, new QueryParameter("@scheduleId", scheduleId));
        if (schedule == null) return null;

        var routeStops = await _adoTemplate.QueryAsync(routeStopQuery, MapRowToRouteStopSchedule, new QueryParameter("@scheduleId", scheduleId));
        schedule.RouteStopSchedules = new List<RouteStopSchedule>(routeStops);

        return schedule;
    }

    public async Task<IEnumerable<Schedule>> GetAllSchedulesAsync()
    {
        string query = "SELECT * FROM schedule";

        return await _adoTemplate.QueryAsync(query, MapRowToSchedule);
    }
    
    public async Task<IEnumerable<Schedule>> FindSchedulesBetweenStopsAsync(int startStopId, int endStopId)
    {
        string query = @"
        SELECT DISTINCT s.*
        FROM schedule s
        JOIN route_stop_schedule rss_start ON s.schedule_id = rss_start.schedule_id
        JOIN route_stop_schedule rss_end ON s.schedule_id = rss_end.schedule_id
        WHERE rss_start.stop_id = @startStopId
          AND rss_end.stop_id = @endStopId
          AND rss_start.sequence_number < rss_end.sequence_number";

        return await _adoTemplate.QueryAsync(query, MapRowToSchedule,
            new QueryParameter("@startStopId", startStopId),
            new QueryParameter("@endStopId", endStopId));
    }
    
    public async Task<IEnumerable<Schedule>> FindSchedulesByTimeAsync(int startStopId, int endStopId, TimeOnly startTime, TimeOnly arrivalTime)
    {
        string query = @"
        SELECT DISTINCT s.*
        FROM schedule s
        JOIN route_stop_schedule rss_start ON s.schedule_id = rss_start.schedule_id
        JOIN route_stop_schedule rss_end ON s.schedule_id = rss_end.schedule_id
        WHERE rss_start.stop_id = @startStopId
          AND rss_end.stop_id = @endStopId
          AND rss_start.sequence_number < rss_end.sequence_number
          AND rss_start.time >= @startTime
          AND rss_end.time <= @arrivalTime";

        return await _adoTemplate.QueryAsync(query, MapRowToSchedule,
            new QueryParameter("@startStopId", startStopId),
            new QueryParameter("@endStopId", endStopId),
            new QueryParameter("@startTime", startTime),
            new QueryParameter("@arrivalTime", arrivalTime));
    }
    
    public async Task<IEnumerable<Schedule>> GetNextConnectionsAsync(int stopId, DateTime dateTime, int count)
    {
        string query = @"
        SELECT DISTINCT s.*, rss.time AS stop_time
        FROM schedule s
        JOIN route_stop_schedule rss ON s.schedule_id = rss.schedule_id
        WHERE rss.stop_id = @stopId
          AND (s.date > @date OR (s.date = @date AND rss.time > @time))
        ORDER BY s.date, rss.time
        LIMIT @count;";

        return await _adoTemplate.QueryAsync(query, reader =>
            {
                var schedule = MapRowToSchedule(reader);
                schedule.RouteStopSchedules = new List<RouteStopSchedule>
                {
                    new RouteStopSchedule
                    {
                        Time = TimeOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("time")))
                    }
                };
                return schedule;
            },
            new QueryParameter("@stopId", stopId),
            new QueryParameter("@date", dateTime.Date),
            new QueryParameter("@time", TimeOnly.FromDateTime(dateTime)),
            new QueryParameter("@count", count));
    }
    
    public async Task<int> SaveCheckInAsync(CheckIn checkIn)
    {
        string query = @"
            INSERT INTO check_in (schedule_id, route_id, stop_id, datetime, api_key)
            VALUES (@scheduleId, @routeId, @stopId, @dateTime, @apiKey)
            RETURNING check_in_id;";

        return await _adoTemplate.ExecuteScalarAsync<int>(query,
            new QueryParameter("@scheduleId", checkIn.ScheduleId),
            new QueryParameter("@routeId", checkIn.RouteId),
            new QueryParameter("@stopId", checkIn.StopId),
            new QueryParameter("@dateTime", checkIn.DateTime),
            new QueryParameter("@apiKey", checkIn.ApiKey));
    }

    public async Task<bool> ValidatePlausibilityAsync(int scheduleId, int routeId, int stopId)
    {
        string query = @"
            SELECT COUNT(*) 
            FROM schedule s
            JOIN route_stop_schedule rss ON s.schedule_id = rss.schedule_id
            WHERE s.schedule_id = @scheduleId AND rss.route_id = @routeId AND rss.stop_id = @stopId;";

        int count = await _adoTemplate.ExecuteScalarAsync<int>(query,
            new QueryParameter("@scheduleId", scheduleId),
            new QueryParameter("@routeId", routeId),
            new QueryParameter("@stopId", stopId));

        return count > 0;
    }
}
