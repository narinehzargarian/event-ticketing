import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { FormEvent, useCallback, useEffect, useState } from "react";

const API = import.meta.env.VITE_API_URL ?? "http://localhost:5080";

interface EventInfo { id: number; title: string; venue: string; startsAt: string; totalSeats: number; availableSeats: number }
interface Seat { id: number; row: number; number: number; priceCents: number; status: "Available" | "Held" | "Sold" }
interface Hold { id: string; eventId: number; seatCount: number; expiresAt: string }
interface Auth { token: string; email: string; role: string }
interface SeatChange { eventId: number; seatIds: number[]; status: Seat["status"] }

const money = (cents: number) => new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" }).format(cents / 100);

async function request<T>(path: string, token: string | null, method = "GET", body?: unknown): Promise<T> {
  const response = await fetch(`${API}${path}`, {
    method,
    headers: {
      ...(body === undefined ? {} : { "content-type": "application/json" }),
      ...(token ? { authorization: `Bearer ${token}` } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new Error(problem?.detail ?? problem?.title ?? "Something went wrong. Please try again.");
  }
  return response.status === 204 ? (undefined as T) : response.json();
}

function Login({ onLogin }: { onLogin: (auth: Auth) => void }) {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);

  const submit = async (path: "login" | "register") => {
    try {
      onLogin(await request<Auth>(`/api/auth/${path}`, null, "POST", { email, password }));
    } catch (failure) {
      setError((failure as Error).message);
    }
  };

  return (
    <form className="login" onSubmit={(event: FormEvent<HTMLFormElement>) => { event.preventDefault(); void submit("login"); }}>
      <label>Email <input type="email" required value={email} onChange={(e) => setEmail(e.target.value)} /></label>
      <label>Password <input type="password" required minLength={8} value={password} onChange={(e) => setPassword(e.target.value)} /></label>
      <div className="login-actions">
        <button type="submit">Log in</button>
        <button type="button" className="secondary" onClick={() => void submit("register")}>Create account</button>
      </div>
      {error && <p className="error" role="alert">{error}</p>}
    </form>
  );
}

export default function App() {
  const [auth, setAuth] = useState<Auth | null>(null);
  const [events, setEvents] = useState<EventInfo[]>([]);
  const [eventId, setEventId] = useState<number | null>(null);
  const [seats, setSeats] = useState<Seat[]>([]);
  const [selected, setSelected] = useState<number[]>([]);
  const [hold, setHold] = useState<Hold | null>(null);
  const [secondsLeft, setSecondsLeft] = useState(0);
  const [message, setMessage] = useState<string | null>(null);

  const loadSeats = useCallback(async (id: number) => {
    setSeats(await request<Seat[]>(`/api/events/${id}/seats`, null));
  }, []);

  useEffect(() => {
    request<EventInfo[]>("/api/events", null)
      .then((list) => {
        setEvents(list);
        setEventId((current) => current ?? list[0]?.id ?? null);
      })
      .catch(() => setMessage("Can't reach the API. Is it running?"));
  }, []);

  useEffect(() => {
    if (eventId === null) return;
    void loadSeats(eventId);
    setSelected([]);
    const connection = new HubConnectionBuilder()
      .withUrl(`${API}/hubs/seats`)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    connection.on("SeatsChanged", (change: SeatChange) => {
      setSeats((current) => current.map((seat) => (change.seatIds.includes(seat.id) ? { ...seat, status: change.status } : seat)));
      setSelected((current) => (change.status === "Available" ? current : current.filter((id) => !change.seatIds.includes(id))));
    });
    // A reconnect may have missed updates, so reload rather than trust the old picture.
    connection.onreconnected(() => {
      void connection.invoke("WatchEvent", eventId);
      void loadSeats(eventId);
    });
    connection.start().then(() => connection.invoke("WatchEvent", eventId)).catch(() => setMessage("Live updates are unavailable."));
    return () => void connection.stop();
  }, [eventId, loadSeats]);

  useEffect(() => {
    if (!hold) return;
    const tick = () => {
      const left = Math.max(0, Math.round((new Date(hold.expiresAt).getTime() - Date.now()) / 1000));
      setSecondsLeft(left);
      if (left === 0) {
        setHold(null);
        setMessage("Your hold ended, so those seats were released.");
      }
    };
    tick();
    const timer = setInterval(tick, 1000);
    return () => clearInterval(timer);
  }, [hold]);

  const rows = [...new Set(seats.map((seat) => seat.row))].sort((a, b) => a - b);
  const total = seats.filter((seat) => selected.includes(seat.id)).reduce((sum, seat) => sum + seat.priceCents, 0);

  const holdSeats = async () => {
    if (!auth || eventId === null) return;
    try {
      setHold(await request<Hold>(`/api/events/${eventId}/holds`, auth.token, "POST", { seatIds: selected }));
      setSelected([]);
      setMessage(null);
    } catch (failure) {
      setMessage((failure as Error).message);
      void loadSeats(eventId);
    }
  };

  const purchase = async () => {
    if (!auth || !hold) return;
    try {
      const order = await request<{ id: number; totalCents: number }>(`/api/holds/${hold.id}/purchase`, auth.token, "POST");
      setMessage(`Order #${order.id} confirmed: ${money(order.totalCents)}. This is a demo, so no real payment was taken.`);
      setHold(null);
    } catch (failure) {
      setMessage((failure as Error).message);
      setHold(null);
    }
  };

  const release = async () => {
    if (!auth || !hold) return;
    await request(`/api/holds/${hold.id}`, auth.token, "DELETE").catch(() => undefined);
    setHold(null);
  };

  const toggle = (seat: Seat) => {
    setSelected((current) => {
      if (current.includes(seat.id)) return current.filter((id) => id !== seat.id);
      return current.length >= 8 ? current : [...current, seat.id];
    });
  };

  return (
    <main>
      <header>
        <h1>Event Tickets</h1>
        {auth && <p className="meta">{auth.email}</p>}
      </header>

      {message && <p className="message" role="status">{message}</p>}

      {!auth && <Login onLogin={setAuth} />}

      <label className="picker">
        Event
        <select value={eventId ?? ""} onChange={(e) => setEventId(Number(e.target.value))}>
          {events.map((event) => <option key={event.id} value={event.id}>{event.title}, {event.venue}</option>)}
        </select>
      </label>

      <div className="stage">Stage</div>
      <section className="seats" aria-label="Seat map">
        {rows.map((row) => (
          <div className="row" key={row}>
            <span className="row-label">{row}</span>
            {seats.filter((seat) => seat.row === row).map((seat) => (
              <button
                key={seat.id}
                className={`seat ${selected.includes(seat.id) ? "selected" : seat.status.toLowerCase()}`}
                disabled={seat.status !== "Available" || !auth || hold !== null}
                aria-pressed={selected.includes(seat.id)}
                aria-label={`Row ${row} seat ${seat.number}, ${money(seat.priceCents)}, ${seat.status}`}
                onClick={() => toggle(seat)}
              >
                {seat.number}
              </button>
            ))}
          </div>
        ))}
      </section>

      <ul className="legend">
        <li><span className="seat available" /> Available</li>
        <li><span className="seat selected" /> Selected</li>
        <li><span className="seat held" /> Held</li>
        <li><span className="seat sold" /> Sold</li>
      </ul>

      {hold ? (
        <section className="panel">
          <p><strong>{hold.seatCount} seat(s) held.</strong> {Math.floor(secondsLeft / 60)}:{String(secondsLeft % 60).padStart(2, "0")} left to complete your purchase.</p>
          <div className="login-actions">
            <button onClick={() => void purchase()}>Complete demo purchase</button>
            <button className="secondary" onClick={() => void release()}>Release seats</button>
          </div>
        </section>
      ) : (
        auth && selected.length > 0 && (
          <section className="panel">
            <p>{selected.length} seat(s) selected: <strong>{money(total)}</strong></p>
            <button onClick={() => void holdSeats()}>Hold seats</button>
          </section>
        )
      )}
    </main>
  );
}
