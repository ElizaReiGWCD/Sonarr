using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.Download;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Housekeeping;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Instrumentation.Commands;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Tv.Commands;
using NzbDrone.Core.Update.Commands;

namespace NzbDrone.Core.Jobs
{
    public interface ITaskManager
    {
        IList<ScheduledTask> GetPending();
        List<ScheduledTask> GetAll();
    }

    public class TaskManager : ITaskManager, IHandle<ApplicationStartedEvent>, IHandle<CommandExecutedEvent>, IHandleAsync<ConfigSavedEvent>
    {
        private readonly IScheduledTaskRepository _scheduledTaskRepository;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public TaskManager(IScheduledTaskRepository scheduledTaskRepository, IConfigService configService, Logger logger)
        {
            _scheduledTaskRepository = scheduledTaskRepository;
            _configService = configService;
            _logger = logger;
        }

        public IList<ScheduledTask> GetPending()
        {
            return _scheduledTaskRepository.All()
                                           .Where(c => c.Interval > 0 && c.LastExecution.AddMinutes(c.Interval) < DateTime.UtcNow)
                                           .ToList();
        }

        public List<ScheduledTask> GetAll()
        {
            return _scheduledTaskRepository.All().ToList();
        }

        public void Handle(ApplicationStartedEvent message)
        {
            var defaultTasks = new[]
                {
                    new ScheduledTask{ Interval = 1, TypeName = typeof(CheckForFinishedDownloadCommand).FullName},
                    new ScheduledTask{ Interval = 6*60, TypeName = typeof(ApplicationUpdateCommand).FullName},
                    new ScheduledTask{ Interval = 3*60, TypeName = typeof(UpdateSceneMappingCommand).FullName},
                    new ScheduledTask{ Interval = 6*60, TypeName = typeof(CheckHealthCommand).FullName},
                    new ScheduledTask{ Interval = 12*60, TypeName = typeof(RefreshSeriesCommand).FullName},
                    new ScheduledTask{ Interval = 24*60, TypeName = typeof(HousekeepingCommand).FullName},
                    new ScheduledTask{ Interval = 7*24*60, TypeName = typeof(BackupCommand).FullName},

                    new ScheduledTask
                    { 
                        Interval = GetRssSyncInterval(),
                        TypeName = typeof(RssSyncCommand).FullName
                    },

                    new ScheduledTask
                    { 
                        Interval = _configService.DownloadedEpisodesScanInterval,
                        TypeName = typeof(DownloadedEpisodesScanCommand).FullName
                    },
                };

            var currentTasks = _scheduledTaskRepository.All().ToList();

            _logger.Trace("Initializing jobs. Available: {0} Existing: {1}", defaultTasks.Count(), currentTasks.Count());

            foreach (var job in currentTasks)
            {
                if (!defaultTasks.Any(c => c.TypeName == job.TypeName))
                {
                    _logger.Trace("Removing job from database '{0}'", job.TypeName);
                    _scheduledTaskRepository.Delete(job.Id);
                }
            }

            foreach (var defaultTask in defaultTasks)
            {
                var currentDefinition = currentTasks.SingleOrDefault(c => c.TypeName == defaultTask.TypeName) ?? defaultTask;

                currentDefinition.Interval = defaultTask.Interval;

                if (currentDefinition.Id == 0)
                {
                    currentDefinition.LastExecution = DateTime.UtcNow;
                }

                _scheduledTaskRepository.Upsert(currentDefinition);
            }
        }

        private int GetRssSyncInterval()
        {
            var interval = _configService.RssSyncInterval;

            if (interval > 0 && interval < 10)
            {
                return 10;
            }

            return interval;
        }

        public void Handle(CommandExecutedEvent message)
        {
            var scheduledTask = _scheduledTaskRepository.All().SingleOrDefault(c => c.TypeName == message.Command.Body.GetType().FullName);

            if (scheduledTask != null)
            {
                _logger.Trace("Updating last run time for: {0}", scheduledTask.TypeName);
                _scheduledTaskRepository.SetLastExecutionTime(scheduledTask.Id, DateTime.UtcNow);
            }
        }

        public void HandleAsync(ConfigSavedEvent message)
        {
            var rss = _scheduledTaskRepository.GetDefinition(typeof(RssSyncCommand));
            rss.Interval = _configService.RssSyncInterval;

            var downloadedEpisodes = _scheduledTaskRepository.GetDefinition(typeof(DownloadedEpisodesScanCommand));
            downloadedEpisodes.Interval = _configService.DownloadedEpisodesScanInterval;

            _scheduledTaskRepository.UpdateMany(new List<ScheduledTask> { rss, downloadedEpisodes });
        }
    }
}
