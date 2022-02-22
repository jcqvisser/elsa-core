using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elsa.Abstractions.Multitenancy;
using Elsa.Models;
using Elsa.Multitenancy;
using Elsa.Options;
using Elsa.Persistence;
using Elsa.Persistence.Specifications.WorkflowInstances;
using Elsa.Providers.WorkflowStorage;
using Elsa.Services;
using Elsa.Services.WorkflowStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Open.Linq.AsyncExtensions;
using IDistributedLockProvider = Elsa.Services.IDistributedLockProvider;

namespace Elsa.StartupTasks
{
    /// <summary>
    /// If there are workflows in the Running state while the server starts, it means the workflow instance never finished execution, e.g. because the workflow host terminated.
    /// This startup task resumes these workflows.
    /// </summary>
    public class ContinueRunningWorkflows : IStartupTask
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IDistributedLockProvider _distributedLockProvider;
        private readonly ElsaOptions _elsaOptions;
        private readonly ILogger _logger;
        private readonly ITenantStore _tenantStore;

        public ContinueRunningWorkflows(
            IDistributedLockProvider distributedLockProvider,
            IServiceScopeFactory scopeFactory,
            ElsaOptions elsaOptions,
            ILogger<ContinueRunningWorkflows> logger,
            ITenantStore tenantStore)
        {
            _distributedLockProvider = distributedLockProvider;
            _scopeFactory = scopeFactory;
            _elsaOptions = elsaOptions;
            _logger = logger;
            _tenantStore = tenantStore;
        }

        public int Order => 1000;

        public async Task ExecuteAsync(CancellationToken cancellationToken = default)
        {
            await using var handle = await _distributedLockProvider.AcquireLockAsync(GetType().Name, _elsaOptions.DistributedLockTimeout, cancellationToken);

            if (handle == null)
                return;

            foreach (var tenant in _tenantStore.GetTenants())
            {
                using var scope = _scopeFactory.CreateScopeForTenant(tenant);

                await ResumeIdleWorkflows(scope.ServiceProvider, tenant, cancellationToken);
                await ResumeRunningWorkflowsAsync(scope.ServiceProvider, tenant, cancellationToken);
            }
        }

        private async Task ResumeIdleWorkflows(IServiceProvider serviceProvider, Tenant tenant, CancellationToken cancellationToken)
        {
            var workflowInstanceStore = serviceProvider.GetRequiredService<IWorkflowInstanceStore>();
            var workflowInstanceDispatcher = serviceProvider.GetRequiredService<IWorkflowInstanceDispatcher>();

            var instances = await workflowInstanceStore.FindManyAsync(new WorkflowStatusSpecification(WorkflowStatus.Idle), cancellationToken: cancellationToken).ToList();

            if (instances.Any())
                _logger.LogInformation("Found {WorkflowInstanceCount} workflows with status 'Idle'. Resuming each one of them", instances.Count);
            else
                _logger.LogInformation("Found no workflows with status 'Id'. Nothing to resume");

            foreach (var instance in instances)
            {
                await using var correlationLockHandle = await _distributedLockProvider.AcquireLockAsync(instance.CorrelationId, _elsaOptions.DistributedLockTimeout, cancellationToken);

                if (correlationLockHandle == null)
                {
                    _logger.LogWarning("Failed to acquire lock on correlation {CorrelationId} for workflow instance {WorkflowInstanceId}", instance.CorrelationId, instance.Id);
                    continue;
                }

                _logger.LogInformation("Resuming {WorkflowInstanceId}", instance.Id);

                var input = await GetWorkflowInputAsync(serviceProvider, instance, cancellationToken);
                await workflowInstanceDispatcher.DispatchAsync(new ExecuteWorkflowInstanceRequest(tenant, instance.Id, Input: input), cancellationToken);
            }
        }

        private async Task ResumeRunningWorkflowsAsync(IServiceProvider serviceProvider, Tenant tenant, CancellationToken cancellationToken)
        {
            var workflowInstanceStore = serviceProvider.GetRequiredService<IWorkflowInstanceStore>();
            var workflowInstanceDispatcher = serviceProvider.GetRequiredService<IWorkflowInstanceDispatcher>();

            var instances = await workflowInstanceStore.FindManyAsync(new WorkflowStatusSpecification(WorkflowStatus.Running), cancellationToken: cancellationToken).ToList();

            if (instances.Any())
                _logger.LogInformation("Found {WorkflowInstanceCount} workflows with status 'Running'. Resuming each one of them", instances.Count);
            else
                _logger.LogInformation("Found no workflows with status 'Running'. Nothing to resume");

            foreach (var instance in instances)
            {
                await using var correlationLockHandle = await _distributedLockProvider.AcquireLockAsync(instance.CorrelationId, _elsaOptions.DistributedLockTimeout, cancellationToken);

                if (correlationLockHandle == null)
                {
                    _logger.LogWarning("Failed to acquire lock on correlation {CorrelationId} for workflow instance {WorkflowInstanceId}", instance.CorrelationId, instance.Id);
                    continue;
                }

                _logger.LogInformation("Resuming {WorkflowInstanceId}", instance.Id);
                var scheduledActivities = instance.ScheduledActivities;

                if (instance.CurrentActivity == null && !scheduledActivities.Any())
                {
                    if (instance.BlockingActivities.Any())
                    {
                        _logger.LogWarning(
                            "Workflow '{WorkflowInstanceId}' was in the Running state, but has no scheduled activities not has a currently executing one. However, it does have blocking activities, so switching to Suspended status",
                            instance.Id);

                        instance.WorkflowStatus = WorkflowStatus.Suspended;
                        await workflowInstanceStore.SaveAsync(instance, cancellationToken);
                        continue;
                    }

                    _logger.LogWarning("Workflow '{WorkflowInstanceId}' was in the Running state, but has no scheduled activities nor has a currently executing one", instance.Id);
                    continue;
                }

                var scheduledActivity = instance.CurrentActivity ?? instance.ScheduledActivities.Peek();
                var input = await GetWorkflowInputAsync(serviceProvider, instance, cancellationToken);
                await workflowInstanceDispatcher.DispatchAsync(new ExecuteWorkflowInstanceRequest(tenant, instance.Id, scheduledActivity.ActivityId, input), cancellationToken);
            }
        }

        private async Task<WorkflowInput?> GetWorkflowInputAsync(IServiceProvider serviceProvider, WorkflowInstance workflowInstance, CancellationToken cancellationToken)
        {
            var workflowStorageService = serviceProvider.GetRequiredService<IWorkflowStorageService>();

            var inputReference = workflowInstance.Input;

            if (inputReference == null)
                return null;

            var inputStorageProviderName = inputReference.ProviderName;
            var input = await workflowStorageService.LoadAsync(inputStorageProviderName, new WorkflowStorageContext(workflowInstance, workflowInstance.DefinitionId), nameof(WorkflowInstance.Input), cancellationToken);

            return new WorkflowInput(input);
        }
    }
}