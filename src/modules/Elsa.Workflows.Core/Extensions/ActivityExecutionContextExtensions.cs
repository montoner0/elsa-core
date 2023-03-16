using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Elsa.Common.Contracts;
using Elsa.Expressions.Contracts;
using Elsa.Expressions.Helpers;
using Elsa.Workflows.Core.Activities.Flowchart.Models;
using Elsa.Workflows.Core.Attributes;
using Elsa.Workflows.Core.Contracts;
using Elsa.Workflows.Core.Models;
using Elsa.Workflows.Core.Signals;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Elsa.Extensions;

/// <summary>
/// Provides extension methods for <see cref="ActivityExecutionContext"/>.
/// </summary>
public static class ActivityExecutionContextExtensions
{
    /// <summary>
    /// Attempts to get a value from the input provided via <see cref="WorkflowExecutionContext"/>. If a value was found, an attempt is made to convert it into the specified type <code>T</code>.
    /// </summary>
    public static bool TryGetInput<T>(this ActivityExecutionContext context, string key, out T value, JsonSerializerOptions? serializerOptions = default)
    {
        var wellKnownTypeRegistry = context.GetRequiredService<IWellKnownTypeRegistry>();

        if (context.Input.TryGetValue(key, out var v))
        {
            value = v.ConvertTo<T>(new ObjectConverterOptions(serializerOptions, wellKnownTypeRegistry))!;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Gets a value from the input provided via <see cref="WorkflowExecutionContext"/>. If a value was found, an attempt is made to convert it into the specified type <code>T</code>.
    /// </summary>
    public static T GetInput<T>(this ActivityExecutionContext context, JsonSerializerOptions? serializerOptions = default) => context.GetInput<T>(typeof(T).Name, serializerOptions);

    /// <summary>
    /// Gets a value from the input provided via <see cref="WorkflowExecutionContext"/>. If a value was found, an attempt is made to convert it into the specified type <code>T</code>.
    /// </summary>
    public static T GetInput<T>(this ActivityExecutionContext context, string key, JsonSerializerOptions? serializerOptions = default)
    {
        var wellKnownTypeRegistry = context.GetRequiredService<IWellKnownTypeRegistry>();
        return context.Input[key].ConvertTo<T>(new ObjectConverterOptions(serializerOptions, wellKnownTypeRegistry))!;
    }

    /// <summary>
    /// Returns true if this activity is triggered for the first time and not being resumed.
    /// </summary>
    public static bool IsTriggerOfWorkflow(this ActivityExecutionContext context) => context.WorkflowExecutionContext.TriggerActivityId == context.Activity.Id;

    /// <summary>
    /// Adds a new <see cref="WorkflowExecutionLogEntry"/> to the execution log of the current <see cref="WorkflowExecutionContext"/>.
    /// </summary>
    /// <param name="context">The <see cref="ActivityExecutionContext"/></param> being extended.
    /// <param name="eventName">The name of the event.</param>
    /// <param name="message">The message of the event.</param>
    /// <param name="source">The source of the activity. For example, the source file name and line number in case of composite activities.</param>
    /// <param name="payload">Any contextual data related to this event.</param>
    /// <param name="includeActivityState">True to include activity state with this event, false otherwise.</param>
    /// <returns>Returns the created <see cref="WorkflowExecutionLogEntry"/>.</returns>
    public static WorkflowExecutionLogEntry AddExecutionLogEntry(this ActivityExecutionContext context, string eventName, string? message = default, string? source = default, object? payload = default, bool includeActivityState = false)
    {
        var activity = context.Activity;
        var activityInstanceId = context.Id;
        var parentActivityInstanceId = context.ParentActivityExecutionContext?.Id;
        var workflowExecutionContext = context.WorkflowExecutionContext;
        var now = context.GetRequiredService<ISystemClock>().UtcNow;

        if (source == null && activity.Source != null)
            source = $"{Path.GetFileName(activity.Source)}:{activity.Line}";

        var activityState = includeActivityState ? context.ActivityState : default;

        var logEntry = new WorkflowExecutionLogEntry(
            activityInstanceId,
            parentActivityInstanceId,
            activity.Id,
            activity.Type,
            context.NodeId,
            activityState,
            now,
            eventName,
            message,
            source,
            payload);

        workflowExecutionContext.ExecutionLog.Add(logEntry);
        return logEntry;
    }

    public static Variable SetVariable(this ActivityExecutionContext context, string name, object? value) => context.ExpressionExecutionContext.SetVariable(name, value);
    public static T? GetVariable<T>(this ActivityExecutionContext context, string name) => context.ExpressionExecutionContext.GetVariable<T?>(name);

    /// <summary>
    /// Returns a dictionary of variable keys and their values across scopes.
    /// </summary>
    public static IDictionary<string, object> GetVariableValues(this ActivityExecutionContext activityExecutionContext) => activityExecutionContext.ExpressionExecutionContext.ReadAndFlattenMemoryBlocks();

    /// <summary>
    /// Evaluates each input property of the activity.
    /// </summary>
    public static async Task EvaluateInputPropertiesAsync(this ActivityExecutionContext context)
    {
        var activity = context.Activity;
        var activityRegistry = context.GetRequiredService<IActivityRegistry>();
        var activityDescriptor = activityRegistry.Find(activity.Type) ?? throw new Exception("Activity descriptor not found");

        var wrappedInputs = activityDescriptor
            .GetWrappedInputProperties(activity)
            .Where(x => x.Value is { MemoryBlockReference: { } })
            .ToDictionary(x => x.Key, x => x.Value);
        
        var evaluator = context.GetRequiredService<IExpressionEvaluator>();
        var stateSerializer = context.GetRequiredService<IActivityStateSerializer>();
        var expressionExecutionContext = context.ExpressionExecutionContext;

        foreach (var input in wrappedInputs)
        {
            var memoryReference = input.Value!.MemoryBlockReference();
            var value = await evaluator.EvaluateAsync(input.Value, expressionExecutionContext);
            memoryReference.Set(context, value);

            // Store the evaluated input value in the activity state.
            var serializedValue = await stateSerializer.SerializeAsync(value);
            
            if(serializedValue.ValueKind != JsonValueKind.Undefined)
                context.ActivityState[input.Key] = serializedValue;
        }
        
        context.SetHasEvaluatedProperties();
    }

    /// <summary>
    /// Evaluates the specified input property of the activity.
    /// </summary>
    public static async Task<T?> EvaluateInputPropertyAsync<TActivity, T>(this ActivityExecutionContext context, Expression<Func<TActivity, Input<T>>> propertyExpression)
    {
        var inputName = propertyExpression.GetProperty()!.Name;
        var input = await EvaluateInputPropertyAsync(context, inputName);
        return context.Get((Input<T>)input);
    }

    /// <summary>
    /// Evaluates a specific input property of the activity.
    /// </summary>
    public static async Task<Input> EvaluateInputPropertyAsync(this ActivityExecutionContext context, string inputName)
    {
        var activity = context.Activity;
        var activityRegistry = context.GetRequiredService<IActivityRegistry>();
        var activityDescriptor = activityRegistry.Find(activity.Type) ?? throw new Exception("Activity descriptor not found");
        var input = activityDescriptor.GetWrappedInputProperty(activity, inputName);

        if (input == null)
            throw new Exception($"No input with name {inputName} could be found");

        if (input.MemoryBlockReference == null!)
            throw new Exception("Input not initialized");

        var evaluator = context.GetRequiredService<IExpressionEvaluator>();
        var expressionExecutionContext = context.ExpressionExecutionContext;
        var memoryBlockReference = input.MemoryBlockReference();
        var value = await evaluator.EvaluateAsync(input, expressionExecutionContext);
        memoryBlockReference.Set(context, value);

        return input;
    }

    /// <summary>
    /// Schedules the specified activity with the provided callback.
    /// If the activity is null, the callback is invoked immediately.
    /// </summary>
    public static async Task ScheduleOutcomeAsync(this ActivityExecutionContext context, IActivity? activity, [CallerArgumentExpression("activity")] string portPropertyName = default!)
    {
        if (activity == null)
        {
            var outcome = context.GetOutcomeName(portPropertyName);
            await context.CompleteActivityWithOutcomesAsync(outcome);
            return;
        }

        await context.ScheduleActivityAsync(activity, context);
    }

    public static string GetOutcomeName(this ActivityExecutionContext context, string portPropertyName)
    {
        var owner = context.Activity;
        var ports = owner.GetType().GetProperties().Where(x => typeof(IActivity).IsAssignableFrom(x.PropertyType)).ToList();

        var portQuery =
            from p in ports
            where p.Name == portPropertyName
            select p;

        var portProperty = portQuery.First();
        return portProperty.GetCustomAttribute<PortAttribute>()?.Name ?? portProperty.Name;
    }

    public static async Task<T?> EvaluateAsync<T>(this ActivityExecutionContext context, Input<T> input)
    {
        var evaluator = context.GetRequiredService<IExpressionEvaluator>();
        var memoryBlockReference = input.MemoryBlockReference();
        var value = await evaluator.EvaluateAsync(input, context.ExpressionExecutionContext);
        memoryBlockReference.Set(context, value);
        return value;
    }

    /// <summary>
    /// Returns a flattened list of the current context's ancestors.
    /// </summary>
    /// <returns></returns>
    public static IEnumerable<ActivityExecutionContext> GetAncestors(this ActivityExecutionContext context)
    {
        var current = context.ParentActivityExecutionContext;

        while (current != null)
        {
            yield return current;
            current = current.ParentActivityExecutionContext;
        }
    }

    /// <summary>
    /// Returns a flattened list of the current context's immediate children.
    /// </summary>
    /// <returns></returns>
    public static IEnumerable<ActivityExecutionContext> GetChildren(this ActivityExecutionContext context) =>
        context.WorkflowExecutionContext.ActivityExecutionContexts.Where(x => x.ParentActivityExecutionContext == context);

    /// <summary>
    /// Removes all child <see cref="ActivityExecutionContext"/> objects.
    /// </summary>
    public static async Task RemoveChildrenAsync(this ActivityExecutionContext context)
    {
        // Detach child activity execution contexts.
        await context.WorkflowExecutionContext.RemoveActivityExecutionContextsAsync(context.GetChildren());
    }

    /// <summary>
    /// Send a signal up the current branch.
    /// </summary>
    public static async ValueTask SendSignalAsync(this ActivityExecutionContext context, object signal)
    {
        var ancestorContexts = new[] { context }.Concat(context.GetAncestors());

        foreach (var ancestorContext in ancestorContexts)
        {
            var signalContext = new SignalContext(ancestorContext, context, context.CancellationToken);

            if (ancestorContext.Activity is not ISignalHandler handler)
                continue;

            await handler.HandleSignalAsync(signal, signalContext);

            if (signalContext.StopPropagationRequested)
                return;
        }
    }

    /// <summary>
    /// Complete the current activity. This should only be called by activities that explicitly suppress automatic-completion.
    /// </summary>
    public static async ValueTask CompleteActivityAsync(this ActivityExecutionContext context, object? result = default)
    {
        // Send a signal.
        await context.SendSignalAsync(new ActivityCompleted(result));

        // Remove the context.
        await context.WorkflowExecutionContext.RemoveActivityExecutionContextAsync(context);
    }

    /// <summary>
    /// Complete the current activity with the specified outcome.
    /// </summary>
    public static ValueTask CompleteActivityWithOutcomesAsync(this ActivityExecutionContext context, params string[] outcomes) => context.CompleteActivityAsync(new Outcomes(outcomes));

    /// <summary>
    /// Complete the current composite activity with the specified outcome.
    /// </summary>
    public static async ValueTask CompleteCompositeAsync(this ActivityExecutionContext context, params string[] outcomes) => await context.SendSignalAsync(new CompleteCompositeSignal(new Outcomes(outcomes)));

    /// <summary>
    /// Cancel the activity. For blocking activities, it means their bookmarks will be removed. For job activities, the background work will be cancelled.
    /// </summary>
    public static async Task CancelActivityAsync(this ActivityExecutionContext context)
    {
        context.ClearBookmarks();
        await context.SendSignalAsync(new CancelSignal());
    }

    public static ILogger GetLogger(this ActivityExecutionContext context) => (ILogger)context.GetRequiredService(typeof(ILogger<>).MakeGenericType(context.Activity.GetType()));

    internal static bool GetHasEvaluatedProperties(this ActivityExecutionContext context) => context.TransientProperties.TryGetValue<bool>("HasEvaluatedProperties", out var value) && value;
    internal static void SetHasEvaluatedProperties(this ActivityExecutionContext context) => context.TransientProperties["HasEvaluatedProperties"] = true;
}