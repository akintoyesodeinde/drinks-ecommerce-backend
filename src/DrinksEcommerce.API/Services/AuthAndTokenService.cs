using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Application.Contracts;
using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Customers;
using DrinksEcommerce.Persistence;
using DrinksEcommerce.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DrinksEcommerce.API.Services;

public sealed class CurrentUserService(IHttpContextAccessor httpContextAccessor) : ICurrentUserService
{
    public Guid? UserId
    {
        get
        {
            var sub = httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? httpContextAccessor.HttpContext?.User?.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(sub, out var id) ? id : null;
        }
    }

    public string? Email =>
        httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.Email)
        ?? httpContextAccessor.HttpContext?.User?.FindFirstValue(JwtRegisteredClaimNames.Email);

    public string? IpAddress =>
        httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString();

    public IReadOnlyList<string> Roles =>
        httpContextAccessor.HttpContext?.User?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList() ?? [];

    public bool IsInRole(string role) =>
        httpContextAccessor.HttpContext?.User?.IsInRole(role) == true;
}

public sealed class AuthAndAccountService(
    UserManager<ApplicationUser> userManager,
    ApplicationDbContext db,
    INotificationService notifications,
    IOptions<JwtOptions> jwtOptions)
{
    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
        {
            throw new DomainException("First name and last name are required.", "validation_error", 400);
        }
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
        {
            throw new DomainException("A valid email address is required.", "validation_error", 400);
        }

        var normalizedPhone = NigerianValueRules.NormalizeNigerianPhoneOrThrow(request.PhoneNumber);
        var email = request.Email.Trim().ToLowerInvariant();

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            throw DomainException.Conflict("An account with this email address already exists.");
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            PhoneNumber = normalizedPhone,
            EmailConfirmed = false
        };

        var createResult = await userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            var errMsg = string.Join(" ", createResult.Errors.Select(e => e.Description));
            throw new DomainException(errMsg, "validation_error", 400);
        }

        await userManager.AddToRoleAsync(user, Roles.Customer);

        var confirmToken = await userManager.GenerateEmailConfirmationTokenAsync(user);
        await notifications.QueueAsync(
            user.Id,
            email,
            "EmailVerification",
            "Welcome to DrinksEcommerce — Verify Your Email",
            $"Welcome {user.FirstName}! Your email confirmation token is: {confirmToken}",
            null,
            ct);

        return await IssueAuthTokensAsync(user, Guid.NewGuid(), ct);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new DomainException("Email and password are required.", "validation_error", 400);
        }

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            throw new DomainException("Invalid email or password.", "unauthorized", 401);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            throw new DomainException("This account is temporarily locked due to repeated failed sign-in attempts.", "unauthorized", 401);
        }

        var valid = await userManager.CheckPasswordAsync(user, request.Password);
        if (!valid)
        {
            await userManager.AccessFailedAsync(user);
            throw new DomainException("Invalid email or password.", "unauthorized", 401);
        }

        await userManager.ResetAccessFailedCountAsync(user);
        return await IssueAuthTokensAsync(user, Guid.NewGuid(), ct);
    }

    public async Task<AuthResponse> RefreshAsync(string rawRefreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            throw new DomainException("Refresh token is required.", "unauthorized", 401);
        }

        var tokenHash = ComputeTokenHash(rawRefreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
            ?? throw new DomainException("Invalid refresh token.", "unauthorized", 401);

        if (stored.RevokedAt is not null)
        {
            // Token reuse detected -> revoke entire token family
            var familyTokens = await db.RefreshTokens
                .Where(t => t.UserId == stored.UserId && t.FamilyId == stored.FamilyId && t.RevokedAt == null)
                .ToListAsync(ct);

            foreach (var ft in familyTokens)
            {
                ft.Revoke();
            }
            await db.SaveChangesAsync(ct);
            throw new DomainException("Refresh token has already been used; session family revoked for security.", "unauthorized", 401);
        }

        if (stored.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            stored.Revoke();
            await db.SaveChangesAsync(ct);
            throw new DomainException("Refresh token has expired.", "unauthorized", 401);
        }

        var user = await userManager.FindByIdAsync(stored.UserId.ToString())
            ?? throw new DomainException("User not found.", "unauthorized", 401);

        var (newRawRefresh, newRefreshEntity) = CreateRefreshTokenEntity(user.Id, stored.FamilyId);
        stored.Revoke(newRefreshEntity.TokenHash);
        db.RefreshTokens.Add(newRefreshEntity);
        await db.SaveChangesAsync(ct);

        return await BuildAuthResponseWithRefreshTokenAsync(user, newRawRefresh, newRefreshEntity.ExpiresAt);
    }

    public async Task LogoutAsync(string? rawRefreshToken, Guid? userId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            var hash = ComputeTokenHash(rawRefreshToken);
            var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (token is not null)
            {
                token.Revoke();
                await db.SaveChangesAsync(ct);
                return;
            }
        }

        if (userId.HasValue)
        {
            var active = await db.RefreshTokens.Where(t => t.UserId == userId.Value && t.RevokedAt == null).ToListAsync(ct);
            foreach (var t in active)
            {
                t.Revoke();
            }
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task ForgotPasswordAsync(string? email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            return; // Prevent user enumeration
        }

        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        await notifications.QueueAsync(
            user.Id,
            user.Email!,
            "PasswordReset",
            "DrinksEcommerce Password Reset Request",
            $"Use this password reset token to set a new password: {resetToken}",
            null,
            ct);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            throw new DomainException("Email, token, and new password are required.", "validation_error", 400);
        }

        var user = await userManager.FindByEmailAsync(request.Email.Trim())
            ?? throw new DomainException("Password reset failed. Verify your token and try again.", "validation_error", 400);

        var res = await userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!res.Succeeded)
        {
            throw new DomainException(string.Join(" ", res.Errors.Select(e => e.Description)), "validation_error", 400);
        }
    }

    public async Task ConfirmEmailAsync(ConfirmEmailRequest request)
    {
        if (!Guid.TryParse(request.UserId, out var uid) || string.IsNullOrWhiteSpace(request.Token))
        {
            throw new DomainException("Invalid confirmation link.", "validation_error", 400);
        }

        var user = await userManager.FindByIdAsync(uid.ToString())
            ?? throw DomainException.NotFound("Account not found.");

        var res = await userManager.ConfirmEmailAsync(user, request.Token);
        if (!res.Succeeded)
        {
            throw new DomainException("Email confirmation token is invalid or expired.", "validation_error", 400);
        }
    }

    public async Task ResendConfirmationAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw DomainException.NotFound("Account not found.");

        if (user.EmailConfirmed)
        {
            return;
        }

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        await notifications.QueueAsync(
            user.Id,
            user.Email!,
            "EmailVerification",
            "DrinksEcommerce Email Verification",
            $"Your email confirmation token is: {token}",
            null,
            ct);
    }

    public async Task<UserDto> GetProfileAsync(Guid userId)
    {
        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw DomainException.NotFound("User profile not found.");
        var roles = await userManager.GetRolesAsync(user);
        return MapUserDto(user, roles);
    }

    public async Task<UserDto> UpdateProfileAsync(Guid userId, UpdateProfileRequest request)
    {
        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw DomainException.NotFound("User profile not found.");

        if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
        {
            throw new DomainException("First name and last name are required.", "validation_error", 400);
        }

        user.FirstName = request.FirstName.Trim();
        user.LastName = request.LastName.Trim();
        user.PhoneNumber = NigerianValueRules.NormalizeNigerianPhoneOrThrow(request.PhoneNumber);

        await userManager.UpdateAsync(user);
        var roles = await userManager.GetRolesAsync(user);
        return MapUserDto(user, roles);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request)
    {
        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw DomainException.NotFound("User profile not found.");

        var res = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!res.Succeeded)
        {
            throw new DomainException(string.Join(" ", res.Errors.Select(e => e.Description)), "validation_error", 400);
        }
    }

    public async Task<IReadOnlyList<AddressDto>> GetAddressesAsync(Guid userId, CancellationToken ct = default)
    {
        var addresses = await db.CustomerAddresses
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.IsDefault)
            .ThenByDescending(a => a.UpdatedAt)
            .ToListAsync(ct);

        var zones = await db.DeliveryZones.AsNoTracking().Where(z => z.IsActive).ToListAsync(ct);
        return addresses.Select(a => MapAddress(a, zones.Any(z => z.Matches(a.State, a.City)))).ToList();
    }

    public async Task<AddressDto> CreateAddressAsync(Guid userId, AddressInput input, CancellationToken ct = default)
    {
        var existing = await db.CustomerAddresses.Where(a => a.UserId == userId).ToListAsync(ct);
        var makeDefault = input.IsDefault == true || existing.Count == 0;

        if (makeDefault)
        {
            foreach (var addr in existing)
            {
                addr.SetDefault(false);
            }
        }

        var entity = new CustomerAddress(
            userId,
            input.Label,
            input.RecipientName,
            input.PhoneNumber,
            input.State,
            input.City,
            input.Street,
            input.Landmark,
            input.PostalCode,
            input.DeliveryInstructions,
            makeDefault);

        db.CustomerAddresses.Add(entity);
        await db.SaveChangesAsync(ct);

        var serviceable = (await db.DeliveryZones.AsNoTracking().Where(z => z.IsActive).ToListAsync(ct))
            .Any(z => z.Matches(entity.State, entity.City));

        return MapAddress(entity, serviceable);
    }

    public async Task<AddressDto> UpdateAddressAsync(Guid userId, Guid addressId, AddressInput input, CancellationToken ct = default)
    {
        var entity = await db.CustomerAddresses.FirstOrDefaultAsync(a => a.Id == addressId && a.UserId == userId, ct)
            ?? throw DomainException.NotFound("Address not found.");

        if (input.IsDefault == true)
        {
            var others = await db.CustomerAddresses.Where(a => a.UserId == userId && a.Id != addressId).ToListAsync(ct);
            foreach (var o in others)
            {
                o.SetDefault(false);
            }
        }

        entity.Update(
            input.Label,
            input.RecipientName,
            input.PhoneNumber,
            input.State,
            input.City,
            input.Street,
            input.Landmark,
            input.PostalCode,
            input.DeliveryInstructions,
            input.IsDefault ?? entity.IsDefault);

        await db.SaveChangesAsync(ct);

        var serviceable = (await db.DeliveryZones.AsNoTracking().Where(z => z.IsActive).ToListAsync(ct))
            .Any(z => z.Matches(entity.State, entity.City));

        return MapAddress(entity, serviceable);
    }

    public async Task DeleteAddressAsync(Guid userId, Guid addressId, CancellationToken ct = default)
    {
        var entity = await db.CustomerAddresses.FirstOrDefaultAsync(a => a.Id == addressId && a.UserId == userId, ct)
            ?? throw DomainException.NotFound("Address not found.");

        db.CustomerAddresses.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    private async Task<AuthResponse> IssueAuthTokensAsync(ApplicationUser user, Guid familyId, CancellationToken ct)
    {
        var (rawRefresh, refreshEntity) = CreateRefreshTokenEntity(user.Id, familyId);
        db.RefreshTokens.Add(refreshEntity);
        await db.SaveChangesAsync(ct);
        return await BuildAuthResponseWithRefreshTokenAsync(user, rawRefresh, refreshEntity.ExpiresAt);
    }

    private async Task<AuthResponse> BuildAuthResponseWithRefreshTokenAsync(
        ApplicationUser user,
        string rawRefreshToken,
        DateTimeOffset refreshExpiresAt)
    {
        var roles = await userManager.GetRolesAsync(user);
        var opts = jwtOptions.Value;
        var now = DateTimeOffset.UtcNow;
        var accessExpires = now.AddMinutes(Math.Max(5, opts.AccessTokenMinutes));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Name, user.FullName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opts.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(
            issuer: opts.Issuer,
            audience: opts.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: accessExpires.UtcDateTime,
            signingCredentials: creds);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(jwt);

        return new AuthResponse(
            accessToken,
            accessExpires,
            rawRefreshToken,
            refreshExpiresAt,
            MapUserDto(user, roles));
    }

    private (string RawToken, RefreshToken Entity) CreateRefreshTokenEntity(Guid userId, Guid familyId)
    {
        var rawBytes = RandomNumberGenerator.GetBytes(48);
        var rawToken = Convert.ToBase64String(rawBytes);
        var hash = ComputeTokenHash(rawToken);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(Math.Max(1, jwtOptions.Value.RefreshTokenDays));
        return (rawToken, new RefreshToken(userId, hash, familyId, expiresAt));
    }

    public static string ComputeTokenHash(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static UserDto MapUserDto(ApplicationUser u, IList<string> roles) =>
        new(u.Id.ToString(), u.Email ?? string.Empty, u.FirstName, u.LastName, u.PhoneNumber, u.EmailConfirmed, roles.ToList());

    private static AddressDto MapAddress(CustomerAddress a, bool isServiceable) =>
        new(
            a.Id.ToString(),
            a.Label,
            a.RecipientName,
            a.PhoneNumber,
            a.State,
            a.City,
            a.Street,
            a.Landmark,
            a.PostalCode,
            a.DeliveryInstructions,
            a.IsDefault,
            isServiceable);
}
