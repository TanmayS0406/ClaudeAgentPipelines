using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClaudeAgentPipelines.Billing;

public sealed class CashfreeException(string message) : Exception(message);

/// <summary>What the app sends to Cashfree to start a payment.</summary>
public sealed record CashfreeOrderRequest(
    string OrderId,
    decimal Amount,
    string CustomerId,
    string CustomerName,
    string CustomerPhone,
    string? CustomerEmail,
    string ReturnUrl,
    string Note,
    DateTimeOffset ExpiresAt);

/// <summary>An order as Cashfree reports it. Status is ACTIVE, PAID, EXPIRED, TERMINATED or TERMINATION_REQUESTED.</summary>
public sealed record CashfreeOrder(string OrderId, string Status, decimal Amount, string? PaymentSessionId);

/// <summary>
/// The part of the Cashfree Payment Gateway API the app uses (orders, version 2023-08-01): create an order to get a
/// payment session for the JS checkout, and read an order back to see whether it was paid.
/// </summary>
public sealed class CashfreeClient(IHttpClientFactory httpClients, IOptionsMonitor<CashfreeOptions> options)
{
    public const string HttpClientName = "cashfree";

    public bool IsConfigured => options.CurrentValue.IsConfigured;

    public async Task<CashfreeOrder> CreateOrderAsync(CashfreeOrderRequest order, CancellationToken cancellationToken)
    {
        var customer = new JsonObject
        {
            ["customer_id"] = order.CustomerId,
            ["customer_name"] = order.CustomerName,
            ["customer_phone"] = order.CustomerPhone
        };
        if (!string.IsNullOrWhiteSpace(order.CustomerEmail)) customer["customer_email"] = order.CustomerEmail;

        var body = new JsonObject
        {
            ["order_id"] = order.OrderId,
            ["order_amount"] = order.Amount,
            ["order_currency"] = "INR",
            ["order_note"] = order.Note,
            ["order_expiry_time"] = order.ExpiresAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["customer_details"] = customer,
            ["order_meta"] = new JsonObject { ["return_url"] = order.ReturnUrl }
        };

        using var request = CreateRequest(HttpMethod.Post, "/orders");
        request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        return await SendAsync(request, cancellationToken);
    }

    public async Task<CashfreeOrder> GetOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/orders/" + Uri.EscapeDataString(orderId));
        return await SendAsync(request, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var settings = options.CurrentValue;
        if (!settings.IsConfigured)
        {
            throw new CashfreeException(
                "Cashfree is not set up. Set Cashfree:ClientId and Cashfree:ClientSecret (see the README), then try again.");
        }

        var request = new HttpRequestMessage(method, settings.ApiBase + path);
        request.Headers.Add("x-client-id", settings.ClientId);
        request.Headers.Add("x-client-secret", settings.ClientSecret);
        request.Headers.Add("x-api-version", settings.ApiVersion);
        return request;
    }

    private async Task<CashfreeOrder> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        string text;
        try
        {
            using var client = httpClients.CreateClient(HttpClientName);
            response = await client.SendAsync(request, cancellationToken);
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new CashfreeException("Could not reach Cashfree: " + ex.Message);
        }

        using (response)
        {
            JsonNode? json = null;
            try
            {
                json = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // not JSON: reported below
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = (string?)json?["message"] ?? $"Cashfree answered {(int)response.StatusCode} {response.ReasonPhrase}.";
                throw new CashfreeException(response.StatusCode == HttpStatusCode.Unauthorized
                    ? "Cashfree rejected the credentials (" + message + "). Check Cashfree:ClientId, ClientSecret and Environment."
                    : "Cashfree: " + message);
            }

            var orderId = (string?)json?["order_id"];
            var status = (string?)json?["order_status"];
            if (orderId is null || status is null) throw new CashfreeException("Cashfree sent an answer the app does not understand.");

            return new CashfreeOrder(
                orderId,
                status,
                json!["order_amount"]?.GetValue<decimal>() ?? 0,
                (string?)json["payment_session_id"]);
        }
    }
}
