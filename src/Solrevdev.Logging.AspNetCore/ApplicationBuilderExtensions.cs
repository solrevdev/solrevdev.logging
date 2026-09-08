using Microsoft.AspNetCore.Builder;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Pipeline registration for the two log context middleware components.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The required order, and every step of it earns its place:</strong>
/// </para>
/// <code>
/// app.UseForwardedHeaders();          // settles the caller's address and scheme
/// app.UseSolrevdevClientLogContext(); // ClientIp and TraceId, above everything that can short-circuit
/// app.UseSolrevdevRequestLogging();   // the one-line request summary
/// app.UseStaticFiles();
/// app.UseRouting();
/// app.UseRateLimiter();               // refuses an authentication POST before authentication runs
/// app.UseAuthentication();
/// app.UseSolrevdevUserLogContext();   // UserId and UserEmail, in the one-line window
/// app.UseAuthorization();
/// </code>
/// <para>
/// Move the client log context below the rate limiter and a 429 is logged with no address and
/// no trace id. Move the user log context above <c>UseAuthentication</c> and there is no
/// principal to read; move it below <c>UseAuthorization</c> and a refusal no longer names who
/// was refused. Pin both positions with a test in the consuming application.
/// </para>
/// </remarks>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the client and trace log context middleware.
    /// </summary>
    /// <remarks>
    /// Register it immediately AFTER <c>UseForwardedHeaders</c> and BEFORE anything that can
    /// short-circuit the pipeline: the request logging, the exception handler,
    /// <c>UseHttpsRedirection</c>, <c>UseStaticFiles</c> and, above all, the rate limiter.
    /// Above the request logging so the one line per request carries the address; above the
    /// rate limiter so a refusal does too.
    /// </remarks>
    /// <param name="app">The <see cref="IApplicationBuilder"/>.</param>
    /// <returns>The <see cref="IApplicationBuilder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>AddSolrevdevRequestLogging</c> was not called.
    /// </exception>
    public static IApplicationBuilder UseSolrevdevClientLogContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RequestLoggingMarkerService.ThrowIfUnregistered(app);

        return app.UseMiddleware<ClientLogContextMiddleware>();
    }

    /// <summary>
    /// Adds the user log context middleware.
    /// </summary>
    /// <remarks>
    /// Register it AFTER <c>UseAuthentication</c>, because it reads the principal that
    /// authentication settles, and BEFORE <c>UseAuthorization</c>, so that a refusal is logged
    /// with the name of whoever was refused. That window is the only correct slot and it is
    /// one line wide.
    /// </remarks>
    /// <param name="app">The <see cref="IApplicationBuilder"/>.</param>
    /// <returns>The <see cref="IApplicationBuilder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>AddSolrevdevRequestLogging</c> was not called.
    /// </exception>
    public static IApplicationBuilder UseSolrevdevUserLogContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RequestLoggingMarkerService.ThrowIfUnregistered(app);

        return app.UseMiddleware<UserLogContextMiddleware>();
    }
}
