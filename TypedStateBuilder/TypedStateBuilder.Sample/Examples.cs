using System;
using System.Threading.Tasks;

namespace TypedStateBuilder.Sample;

public class Program
{
    public static async Task Main()
    {
        var user = await TypedStateBuilders.CreateUserBuilder<string>().SetName("Alice")
            .SetAge().Build();
        
        Console.WriteLine(user);
    }
}

[TypedStateBuilder]
internal class PersonBuilder
{
    [StepForValue] private string _name;

    [StepForValue] private int _age;

    [Build]
    public Person Build() => new(_name, _age);
}

public sealed record Person(string Name, int Age);

public interface IEmailService
{
    Task<bool> IsValidAsync(string email);
}

[TypedStateBuilder]
internal class InviteBuilder
{
    private readonly IEmailService _emailService;

    public InviteBuilder(IEmailService emailService)
    {
        _emailService = emailService;
    }

    [StepForValue] [ValidateValue(nameof(ValidateEmailAsync))]
    private string _email;

    [StepForValue] private string _message;

    private async Task ValidateEmailAsync(string value)
    {
        if (!await _emailService.IsValidAsync(value))
            throw new InvalidOperationException("Email is not allowed.");
    }

    [Build]
    public Invite Build() => new(_email, _message);
}

public sealed record Invite(string Email, string Message);

[TypedStateBuilder]
internal class OrderBuilder
{
    [StepForValue] private string _customerName;

    [StepForValue] private decimal _netAmount;

    [StepForValue(nameof(DefaultCurrency))]
    private string _currency;

    private static string DefaultCurrency() => "USD";

    [Build]
    public Order Build()
    {
        var normalizedName = _customerName.Trim();
        var tax = Math.Round(_netAmount * 0.25m, 2);
        var gross = _netAmount + tax;

        return new Order(
            normalizedName,
            _netAmount,
            tax,
            gross,
            _currency);
    }
}

public sealed record Order(
    string CustomerName,
    decimal NetAmount,
    decimal Tax,
    decimal GrossAmount,
    string Currency);

[TypedStateBuilder]
internal class UserBuilder<T>
{
    private UserBuilder(Guid id)
    {
        _id = id;
    }

    private UserBuilder() : this(Guid.NewGuid())
    {
    }

    [Build]
    public async Task<User<T>> Build()
    {
        return new User<T>(_id, _name, _age, _email);
    }

    private static string DefaultEmail() => "unknown@example.com";

    private void ValidateNameNotNull(T value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), "Name cannot be null.");
        }
    }

    private static void ValidateNameNotEmptyString(T value)
    {
        if (value is string s && string.IsNullOrWhiteSpace(s))
        {
            throw new ArgumentException("Name cannot be empty or whitespace.", nameof(value));
        }
    }

    private void ValidateAgeNonNegative(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Age cannot be negative.");
        }
    }

    private static Task ValidateAgeReasonableAsync(int value)
    {
        if (value > 150)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Age is unrealistically large.");
        }

        return Task.CompletedTask;
    }

    private async Task ValidateEmailContainsAtAsync(string value)
    {
        await Task.Yield();

        if (string.IsNullOrWhiteSpace(value) || !value.Contains("@", StringComparison.Ordinal))
        {
            throw new ArgumentException("Email must contain '@'.", nameof(value));
        }
    }

    private static void ValidateEmailLength(string value)
    {
        if (value.Length > 320)
        {
            throw new ArgumentException("Email is too long.", nameof(value));
        }
    }

    private int AgeFromString(string age)
    {
        return Int32.Parse(age);
    }

    private int AgeFromDefault()
    {
        return 30;
    }

    [StepForValue] [ValidateValue(nameof(ValidateNameNotNull))] [ValidateValue(nameof(ValidateNameNotEmptyString))]
    public T _name;

    [StepForValue]
    [ValidateValue(nameof(ValidateAgeNonNegative))]
    [ValidateValue(nameof(ValidateAgeReasonableAsync))]
    [StepOverload(nameof(AgeFromString))]
    [StepOverload(nameof(AgeFromDefault))]
    private int _age;

    [StepForValue(nameof(DefaultEmail))]
    [ValidateValue(nameof(ValidateEmailContainsAtAsync))]
    [ValidateValue(nameof(ValidateEmailLength))]
    private string _email;

    private readonly Guid _id;
}

public sealed record User<T>(Guid Id, T Name, int Age, string Email);

[TypedStateBuilder]
public class VehicleBuilder
{
    [StepForValue]
    [StepBranch("car")]
    [ValidateValue(nameof(ValidateEngine))]
    private string _engine;

    [StepForValue(nameof(GetDefaultBatteryKwh))]
    [StepBranch("car/electric")]
    private int _batteryKwh;

    [StepForValue]
    [StepBranch("bike")]
    private bool _hasBell;

    [StepForValue]
    [ValidateValue(nameof(ValidateColor))]
    private string _color;

    [Build("car")]
    public string BuildCar()
        => $"Car: engine={_engine}, color={_color}";

    [Build("car/electric")]
    public string BuildElectricCar()
        => $"Electric car: battery={_batteryKwh}kWh, color={_color}";

    [Build("bike")]
    public string BuildBike()
        => $"Bike: bell={_hasBell}, color={_color}";

    private int GetDefaultBatteryKwh() => 75;

    private void ValidateColor(string color)
    {
        if (string.IsNullOrWhiteSpace(color))
            throw new ArgumentException("Color is required.", nameof(color));
    }

    private void ValidateEngine(string engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
            throw new ArgumentException("Engine is required.", nameof(engine));
    }
}