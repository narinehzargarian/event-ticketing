using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Services;

namespace Ticketing.Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public class AuthController(AppDbContext db, TokenService tokens, IPasswordHasher<User> hasher, IConfiguration config) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        // Anyone can sign up as a customer. Creating events takes a code from the server's configuration.
        var role = Roles.Customer;
        if (!string.IsNullOrEmpty(request.OrganizerCode))
        {
            if (!CodesMatch(request.OrganizerCode, config["Auth:OrganizerCode"]))
            {
                return Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Invalid organizer code.");
            }
            role = Roles.Organizer;
        }

        var user = new User { Email = request.Email.Trim().ToLowerInvariant(), Role = role };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // The unique index decides, so two simultaneous sign-ups can't both succeed.
            return Problem(statusCode: StatusCodes.Status409Conflict, detail: "That email is already registered.");
        }
        return Ok(new AuthResponse(tokens.Create(user), user.Email, user.Role));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);
        // The same response for a wrong email or password, so login can't reveal who has an account.
        if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, detail: "Wrong email or password.");
        }
        return Ok(new AuthResponse(tokens.Create(user), user.Email, user.Role));
    }

    private static bool CodesMatch(string given, string? expected) =>
        !string.IsNullOrEmpty(expected) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
}
