using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SalonCrm.Api.Tests;

[Collection("api")]
public class AdminServicesTests(ApiFactory factory)
{
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static object Valid(string? name = null) => new
    {
        name = name ?? "Test " + Guid.NewGuid().ToString("N")[..8],
        category = "Massage",
        description = "Popis",
        durationMinutes = 45,
        price = 33.5m,
    };

    [Fact]
    public async Task User_role_gets_403_and_anonymous_401_on_every_admin_services_endpoint()
    {
        var (worker, _) = await factory.LoginAsync(AuthTestHelpers.WorkerEmail, ApiFactory.UserPassword);
        using var anon = factory.NewClient();
        var id = Guid.NewGuid();

        foreach (var client in new[] { worker, anon })
        {
            var expected = client == worker ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
            var calls = new List<Func<Task<HttpResponseMessage>>>
            {
                () => client.GetAsync("/api/admin/services"),
                () => client.PostAsJsonAsync("/api/admin/services", Valid()),
                () => client.PutAsJsonAsync($"/api/admin/services/{id}", Valid()),
                () => client.PostAsync($"/api/admin/services/{id}/deactivate", null),
                () => client.PostAsync($"/api/admin/services/{id}/activate", null),
            };
            foreach (var call in calls)
            {
                using var response = await call();
                Assert.Equal(expected, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            }
        }
    }

    [Fact]
    public async Task Create_update_list_roundtrip()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var name = "  Nová služba " + Guid.NewGuid().ToString("N")[..8] + " ";

        var created = await admin.PostAsJsonAsync("/api/admin/services", new
        {
            name, category = "Waxing", description = "  Popis  ", durationMinutes = 30, price = 12.34m,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.ReadJsonAsync();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(name.Trim(), body.GetProperty("name").GetString());
        Assert.Equal("Waxing", body.GetProperty("category").GetString());
        Assert.Equal("Popis", body.GetProperty("description").GetString());
        Assert.Equal(30, body.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(12.34m, body.GetProperty("price").GetDecimal());
        Assert.True(body.GetProperty("isActive").GetBoolean());

        var updated = await admin.PutAsJsonAsync($"/api/admin/services/{id}", new
        {
            name = name.Trim() + " v2", category = "Nails", durationMinutes = 120, price = 0m,
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var u = await updated.ReadJsonAsync();
        Assert.Equal("Nails", u.GetProperty("category").GetString());
        Assert.Equal(120, u.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(0m, u.GetProperty("price").GetDecimal());
        Assert.Equal(JsonValueKind.Null, u.GetProperty("description").ValueKind);

        var list = (await (await admin.GetAsync("/api/admin/services")).ReadJsonAsync()).EnumerateArray().ToList();
        Assert.Contains(list, s => s.GetProperty("id").GetGuid() == id && s.GetProperty("name").GetString() == name.Trim() + " v2");
    }

    [Fact]
    public async Task Deactivate_hides_service_from_public_and_activate_restores_it()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (id, _) = await admin.CreateServiceAsync();
        await admin.CreateEmployeeAsync("Aaa", id);
        using var anon = factory.NewClient();
        var date = BookingTestHelpers.NextWeekday(DayOfWeek.Monday);

        var off = await admin.PostAsync($"/api/admin/services/{id}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await off.ReadJsonAsync()).GetProperty("isActive").GetBoolean());
        var publicList = (await (await anon.GetAsync("/api/public/services")).ReadJsonAsync()).EnumerateArray();
        Assert.DoesNotContain(publicList, s => s.GetProperty("id").GetGuid() == id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/public/availability?serviceId={id}&date={date:yyyy-MM-dd}")).StatusCode);
        var adminList = (await (await admin.GetAsync("/api/admin/services")).ReadJsonAsync()).EnumerateArray();
        Assert.False(adminList.Single(s => s.GetProperty("id").GetGuid() == id).GetProperty("isActive").GetBoolean());

        var on = await admin.PostAsync($"/api/admin/services/{id}/activate", null);

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await on.ReadJsonAsync()).GetProperty("isActive").GetBoolean());
        Assert.NotEmpty((await anon.AvailabilityAsync(id, date)).EnumerateArray());
    }

    [Fact]
    public async Task Unknown_id_is_404_on_put_activate_deactivate()
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/admin/services/{id}", Valid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/admin/services/{id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/admin/services/{id}/activate", null)).StatusCode);
    }

    [Theory]
    [InlineData("""{"name":"X","category":"Skin","durationMinutes":50,"price":10}""")] // not a multiple of 15
    [InlineData("""{"name":"X","category":"Skin","durationMinutes":0,"price":10}""")]
    [InlineData("""{"name":"X","category":"Skin","durationMinutes":495,"price":10}""")]
    [InlineData("""{"name":"X","category":"Skin","durationMinutes":-15,"price":10}""")]
    [InlineData("""{"name":"X","category":"Skin","price":10}""")] // missing duration
    [InlineData("""{"name":"X","category":"Skin","durationMinutes":60,"price":-1}""")]
    [InlineData("""{"name":"X","category":"Skin","durationMinutes":60,"price":10000.01}""")]
    [InlineData("""{"name":"X","category":"Skin","durationMinutes":60,"price":10.123}""")]
    [InlineData("""{"name":"X","category":"Skin","durationMinutes":60}""")] // missing price
    [InlineData("""{"name":"X","durationMinutes":60,"price":10}""")] // missing category
    [InlineData("""{"name":"X","category":1,"durationMinutes":60,"price":10}""")] // numeric category
    [InlineData("""{"name":"X","category":"Bogus","durationMinutes":60,"price":10}""")]
    [InlineData("""{"category":"Skin","durationMinutes":60,"price":10}""")] // missing name
    [InlineData("""{"name":"   ","category":"Skin","durationMinutes":60,"price":10}""")]
    public async Task Invalid_body_is_400_on_create_and_update(string json)
    {
        var (admin, _) = await factory.LoginAdminAsync();
        var (id, _) = await admin.CreateServiceAsync();

        var create = await admin.PostAsync("/api/admin/services", Json(json));
        var update = await admin.PutAsync($"/api/admin/services/{id}", Json(json));

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Equal("application/problem+json", create.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Too_long_name_and_description_are_400_and_boundaries_are_accepted()
    {
        var (admin, _) = await factory.LoginAdminAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/services", new
        { name = new string('x', 201), category = "Skin", durationMinutes = 60, price = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/services", new
        { name = "x", category = "Skin", description = new string('x', 2001), durationMinutes = 60, price = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/admin/services", new
        { name = new string('x', 200), category = "Skin", description = new string('d', 2000), durationMinutes = 15, price = 10000m })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/admin/services", new
        { name = "y", category = "Skin", durationMinutes = 480, price = 0m })).StatusCode);
    }
}

[Collection("api")]
public class CheckConstraintTests(ApiFactory factory)
{
    // Check constraints are evaluated before foreign keys, so random FK values are fine.
    public static TheoryData<string, string> Cases => new()
    {
        {
            "ck_users_role",
            "INSERT INTO users (id, email, password_hash, full_name, role, is_active, created_at) " +
            "VALUES (gen_random_uuid(), 'bad-role@x.test', 'h', 'n', 'Bogus', true, now())"
        },
        {
            "ck_users_role",
            "UPDATE users SET role = 'Bogus' WHERE email = 'admin@salon.local'"
        },
        {
            "ck_services_category",
            "INSERT INTO services (id, name, category, duration_minutes, price, is_active) " +
            "VALUES (gen_random_uuid(), 'n', 'Bogus', 60, 1, true)"
        },
        {
            "ck_services_category",
            "UPDATE services SET category = 'Bogus' WHERE name = 'Relaxačná masáž'"
        },
        {
            "ck_appointments_status",
            "INSERT INTO appointments (id, customer_id, employee_id, service_id, starts_at, ends_at, status, price, source, created_at, updated_at) " +
            "VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), now(), now() + interval '1 hour', 'Bogus', 1, 'Online', now(), now())"
        },
        {
            "ck_appointments_source",
            "INSERT INTO appointments (id, customer_id, employee_id, service_id, starts_at, ends_at, status, price, source, created_at, updated_at) " +
            "VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), now(), now() + interval '1 hour', 'Scheduled', 1, 'Bogus', now(), now())"
        },
        {
            "ck_payments_method",
            "INSERT INTO payments (id, appointment_id, amount, method, paid_at, recorded_by_user_id) " +
            "VALUES (gen_random_uuid(), gen_random_uuid(), 1, 'Bogus', now(), gen_random_uuid())"
        },
        {
            "ck_retention_actions_channel",
            "INSERT INTO retention_actions (id, customer_id, created_by_user_id, channel, note, created_at) " +
            "VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'Bogus', 'n', now())"
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Invalid_enum_value_violates_the_named_check_constraint(string constraint, string sql)
    {
        await using var conn = new Npgsql.NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());

        Assert.Equal("23514", ex.SqlState);
        Assert.Equal(constraint, ex.ConstraintName);
    }
}
