﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace DurableTask.SqlServer.Tests.Utils
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Xunit.Abstractions;

    public static class SharedTestHelpers
    {
        public static string GetTestName(ITestOutputHelper output)
        {
            Type type = output.GetType();
            FieldInfo testMember = type.GetField("test", BindingFlags.Instance | BindingFlags.NonPublic);
            var test = (ITest)testMember.GetValue(output);
            return test.TestCase.TestMethod.Method.Name;
        }

        public static string GetDefaultConnectionString(string database = "DurableDB")
        {
            // The default for local development on a Windows OS
            string defaultConnectionString = $"Server=localhost;Database={database};Trusted_Connection=True;";
            var builder = new SqlConnectionStringBuilder(defaultConnectionString);

            // The use of SA_PASSWORD is intended for use with the mssql docker container
            string saPassword = Environment.GetEnvironmentVariable("SA_PASSWORD");
            if (!string.IsNullOrEmpty(saPassword))
            {
                builder.IntegratedSecurity = false;
                builder.UserID = "sa";
                builder.Password = saPassword;
            }

            // Overrides for ad-hoc testing against alternate setups
            ////builder.IntegratedSecurity = false;
            ////builder.UserID = "sa";
            ////builder.Password = "XXX";
            ////builder.DataSource = "127.0.0.1,14330";
            ////builder.DataSource = "20.190.19.170";

            return builder.ToString();
        }

        public static async Task<object> ExecuteSqlAsync(string commandText, string connectionString = null)
        {
            Exception lastException = null;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    connectionString ??= GetDefaultConnectionString();
                    await using SqlConnection connection = new SqlConnection(connectionString);
                    await using SqlCommand command = connection.CreateCommand();
                    await command.Connection.OpenAsync();

                    command.CommandText = commandText;
                    return await command.ExecuteScalarAsync();
                }
                catch (SqlException e) when (e.Number == 15434)
                {
                    // 15434 : Could not drop login 'XXX' as the user is currently logged in.
                    lastException = e;
                }
                catch (SqlException e) when (e.Number == 6106)
                {
                    // 6106 : Process ID 'XXX' is not an active process ID
                    lastException = e;
                }
            }

            throw lastException;
        }

        public static async Task InitializeDatabaseAsync()
        {
            var options = new SqlOrchestrationServiceSettings(GetDefaultConnectionString());
            var service = new SqlOrchestrationService(options);
            await service.CreateIfNotExistsAsync();
        }

        public static async Task<TestCredential> CreateTaskHubLoginAsync(string prefix)
        {
            // NOTE: Max length for user IDs is 128 characters
            string userId = $"{prefix}_{DateTime.UtcNow:yyyyMMddhhmmssff}";
            string password = GeneratePassword();

            // Generate a low-priviledge user account. This will map to a unique task hub.
            await ExecuteSqlAsync($"CREATE LOGIN [testlogin_{userId}] WITH PASSWORD = '{password}'");
            await ExecuteSqlAsync($"CREATE USER [testuser_{userId}] FOR LOGIN [testlogin_{userId}]");
            await ExecuteSqlAsync($"ALTER ROLE dt_runtime ADD MEMBER [testuser_{userId}]");

            var builder = new SqlConnectionStringBuilder(GetDefaultConnectionString())
            {
                UserID = $"testlogin_{userId}",
                Password = password,
                IntegratedSecurity = false,
            };

            return new TestCredential(userId, builder.ToString());
        }

        public static async Task DropTaskHubLoginAsync(TestCredential credential)
        {
            // Drop the generated user information
            string userId = credential.UserId;
            await ExecuteSqlAsync($"ALTER ROLE dt_runtime DROP MEMBER [testuser_{userId}]");
            await ExecuteSqlAsync($"DROP USER IF EXISTS [testuser_{userId}]");

            // drop all the connections; otherwise, the DROP LOGIN statement will fail
            await ExecuteSqlAsync($"DECLARE @kill varchar(max) = ''; SELECT @kill = @kill + 'KILL ' + CAST(session_id AS varchar(5)) + ';' FROM sys.dm_exec_sessions WHERE original_login_name = 'testlogin_{userId}'; EXEC(@kill);");
            await ExecuteSqlAsync($"DROP LOGIN [testlogin_{userId}]");
        }

        public static Task EnableMultitenancyAsync()
        {
            return ExecuteSqlAsync($"EXECUTE dt.SetGlobalSetting @Name='TaskHubMode', @Value=1");
        }

        static string GeneratePassword()
        {
            const string AllowedChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHJKLMNPQRSTWXYZ0123456789#$";
            const int PasswordLenth = 16;

            string password = GetRandomString(AllowedChars, PasswordLenth);
            while (!MeetsSqlPasswordConstraint(password))
            {
                password = GetRandomString(AllowedChars, PasswordLenth);
            }

            return password;
        }

        static string GetRandomString(string allowedChars, int length)
        {
            var result = new StringBuilder(length);
            byte[] randomBytes = new byte[length * 4];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);

                for (int i = 0; i < length; i++)
                {
                    int seed = BitConverter.ToInt32(randomBytes, i * 4);
                    Random random = new Random(seed);
                    result.Append(allowedChars[random.Next(allowedChars.Length)]);
                }
            }

            return result.ToString();
        }

        public static DateTime GetCurrentDatabaseTimeUtc()
        {
            string connectionString = GetDefaultConnectionString();
            using SqlConnection connection = new SqlConnection(connectionString);
            using SqlCommand command = connection.CreateCommand();
            command.Connection.Open();

            command.CommandText = "SELECT SYSUTCDATETIME()";
            DateTime currentDatabaseTimeUtc = (DateTime)command.ExecuteScalar();
            return DateTime.SpecifyKind(currentDatabaseTimeUtc, DateTimeKind.Utc);
        }

        static bool MeetsSqlPasswordConstraint(string password)
        {
            return !string.IsNullOrEmpty(password) &&
                password.Any(c => char.IsUpper(c)) &&
                password.Any(c => char.IsLower(c)) &&
                password.Any(c => char.IsDigit(c)) &&
                password.Any(c => !char.IsLetterOrDigit(c)) &&
                password.Length >= 8;
        }


        public static TimeSpan AdjustForDebugging(this TimeSpan timeout)
        {
            if (timeout == default)
            {
                timeout = TimeSpan.FromSeconds(10);
            }

            if (Debugger.IsAttached)
            {
                TimeSpan debuggingTimeout = TimeSpan.FromMinutes(5);
                if (debuggingTimeout > timeout)
                {
                    timeout = debuggingTimeout;
                }
            }

            return timeout;
        }

        public static async Task ParallelForEachAsync<T>(this IEnumerable<T> items, int maxConcurrency, Func<T, Task> action)
        {
            List<Task> tasks;
            if (items is ICollection<T> itemCollection)
            {
                tasks = new List<Task>(itemCollection.Count);
            }
            else
            {
                tasks = new List<Task>();
            }

            using var semaphore = new SemaphoreSlim(maxConcurrency);
            foreach (T item in items)
            {
                tasks.Add(InvokeThrottledAction(item, action, semaphore));
            }

            await Task.WhenAll(tasks);
        }

        static async Task InvokeThrottledAction<T>(T item, Func<T, Task> action, SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync();
            try
            {
                await action(item);
            }
            finally
            {
                semaphore.Release();
            }
        }
    }
}
