// Replaces HandleWebhookAsync. Called by the doctor's own browser, right after Razorpay
// Checkout.js hands back these three values on successful payment — no public URL, no
// ngrok, no server-to-server call from Razorpay at all.
//
// Trade-off, stated plainly and not hidden: this depends on the browser actually making
// this call. If the doctor pays successfully on Razorpay's side but closes the tab or loses
// network before this request completes, Razorpay has been paid and this system never finds
// out — the reservation sits ACTIVE until the (not yet built) stock-reservation-expiry job
// eventually releases it, and no Order is ever created. Accepted for this project; a real
// production system would pair this with a reconciliation job that periodically checks
// Razorpay's API for payments with no matching completed Order.
public async Task<PaymentStatusResponse> ConfirmPaymentAsync(int doctorId, PaymentConfirmRequest request)
{
    if (!VerifyPaymentSignature(request.RazorpayOrderId, request.RazorpayPaymentId, request.RazorpaySignature))
        throw new InvalidPaymentSignatureException();

    var paymentIntent = await _paymentIntentRepository.GetByRazorpayOrderIdAsync(request.RazorpayOrderId)
        ?? throw new NotFoundException($"PaymentIntent for Razorpay order {request.RazorpayOrderId} not found.");

    if (paymentIntent.DoctorId != doctorId)
        throw new NotFoundException($"PaymentIntent for Razorpay order {request.RazorpayOrderId} not found.");

    // Idempotency — a doctor could double-click confirm, or the client could retry on a
    // flaky connection. If this intent is no longer CREATED, it's already been processed;
    // just return the current state rather than creating a duplicate Order.
    if (paymentIntent.Status != PaymentIntentStatus.CREATED.ToString())
        return new PaymentStatusResponse(
            paymentIntent.Id,
            Enum.Parse<PaymentIntentStatus>(paymentIntent.Status, ignoreCase: true),
            paymentIntent.Order?.Id);

    paymentIntent.Status = PaymentIntentStatus.PAID.ToString();
    paymentIntent.RazorpayPaymentId = request.RazorpayPaymentId;
    paymentIntent.UpdatedAt = DateTime.UtcNow;

    var snapshotItems = JsonSerializer.Deserialize<List<SnapshotItem>>(paymentIntent.ItemsSnapshot)!;

    var order = new Order
    {
        PaymentIntentId = paymentIntent.Id,
        DoctorId = paymentIntent.DoctorId,
        DoctorNameSnapshot = paymentIntent.DoctorNameSnapshot,
        Status = OrderStatus.NEW.ToString(),
        TotalAmount = paymentIntent.Amount,
        CreatedAt = DateTime.UtcNow,
        OrderItems = snapshotItems.Select(i => new OrderItem
        {
            DrugId = i.DrugId,
            Quantity = i.Quantity,
            UnitPriceAtOrder = i.UnitPrice
        }).ToList()
    };

    await _orderRepository.AddAsync(order);
    await _orderRepository.SaveChangesAsync();

    return new PaymentStatusResponse(paymentIntent.Id, PaymentIntentStatus.PAID, order.Id);
}

// Razorpay's client-confirm signature: HMAC-SHA256 of "order_id|payment_id", using
// KeySecret — NOT WebhookSecret, which this flow no longer needs at all.
private bool VerifyPaymentSignature(string razorpayOrderId, string razorpayPaymentId, string signature)
{
    var secret = _config["Razorpay:KeySecret"]!;
    var payload = $"{razorpayOrderId}|{razorpayPaymentId}";
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(computedSignature), Encoding.UTF8.GetBytes(signature));
}





[HttpPost("confirm")]
[Authorize(Roles = "DOCTOR")]
public async Task<ActionResult<PaymentStatusResponse>> Confirm([FromBody] PaymentConfirmRequest request)
{
    var doctorId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    return Ok(await _paymentService.ConfirmPaymentAsync(doctorId, request));
}

