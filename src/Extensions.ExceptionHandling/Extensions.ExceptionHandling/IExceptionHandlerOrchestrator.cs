﻿using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Extensions.ExceptionHandling
{
    /// <summary>
    /// Represents an exception handling orchestrator which job is to route the exception to the right handler, or fail gracefully.
    /// </summary>
    public interface IExceptionHandlerOrchestrator
    {
        /// <summary>
        /// Attempt to handle the incoming exception.
        /// </summary>
        /// <param name="exception">The exception to handle.</param>
        /// <param name="context">The http context in which the exception occurred.</param>
        /// <returns>True if the exception were handled successfully, otherwise false.</returns>
        Task<bool> TryHandleExceptionAsync(Exception exception, HttpContext context);
    }

    /// <summary>
    /// Concrete exception handling orchestrator which job is to route the exception to the right handler, or fail gracefully.
    /// </summary>
    public sealed class ExceptionHandlerOrchestrator : IExceptionHandlerOrchestrator
    {
        private readonly ILogger<ExceptionHandlerOrchestrator> _logger;

        /// <summary>
        /// Initialize a new instance of the <see cref="ExceptionHandlerOrchestrator"/> class. 
        /// </summary>
        /// <param name="logger"></param>
        public ExceptionHandlerOrchestrator(ILogger<ExceptionHandlerOrchestrator> logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// Attempt to handle the incoming exception.
        /// </summary>
        /// <param name="exception">The exception to handle.</param>
        /// <param name="context">The http context in which the exception occurred.</param>
        /// <returns>True if the exception were handled successfully, otherwise false.</returns>
        public async Task<bool> TryHandleExceptionAsync(Exception exception, HttpContext context)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.RequestServices == null)
            {
                throw new ArgumentException($"{nameof(context)}.{nameof(context.RequestServices)} cannot be null.", nameof(context));
            }

            var handlerContext = CreateHandlerContext(context);
            var exceptionType = exception.GetType();
            
            // TODO: Add continuation strategy
            var exceptionHandlers = context.RequestServices.GetExceptionHandlers(exceptionType).Take(1);

            var problemDetails = await exceptionHandlers
                .Select(handler => InvokeHandler(handler, exception, exceptionType, handlerContext))
                .FirstOrDefaultAsync(x => x != null);

            if (problemDetails == null)
            {
                return false;
            }

            await WriteResponseAsync(context, problemDetails);

            return true;
        }

        private static ExceptionHandlerContext CreateHandlerContext(HttpContext context)
        {
            return new ExceptionHandlerContext(context.TraceIdentifier);
        }

        private static async Task WriteResponseAsync(HttpContext context, ProblemDetails problemDetails)
        {
            problemDetails.Status = problemDetails.Status ?? StatusCodes.Status500InternalServerError;
            context.Response.Clear();
            context.Response.StatusCode = problemDetails.Status.Value;
            await context.Response.WriteJsonAsync(problemDetails);
        }

        private async Task<ProblemDetails> InvokeHandler(object exceptionHandler, Exception exception, Type exceptionType, ExceptionHandlerContext handlerContext)
        {
            try
            {
                return await (Task<ProblemDetails>)InvokeGenericHandlerMethodInfo
                    .MakeGenericMethod(exceptionType)
                    .Invoke(null, new[] { exceptionHandler, exception, handlerContext });
            }
            catch (Exception e)
            {
                var handlerType = exceptionHandler.GetType();
                _logger?.LogError(e, $"Handler of type {handlerType.FullName} threw an unexpected error. " +
                                    "Exception handler middleware will not handle this exception.");
            }

            return default;
        }

        private static readonly MethodInfo InvokeGenericHandlerMethodInfo = typeof(ExceptionHandlerOrchestrator)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(x => x.IsGenericMethod && x.Name == nameof(InvokeGenericHandler));

        private static Task<ProblemDetails> InvokeGenericHandler<TException>(
            IExceptionHandler<TException> exceptionHandler, 
            TException exception,
            ExceptionHandlerContext handlerContext)
            where TException : Exception
        {
            return exceptionHandler.Handle(exception, handlerContext);
        }
    }
}
