const { WebSocketServer } = require("ws");

const wss = new WebSocketServer({ port: 44333 });

class ClientConnection {
    IsClosed = false;
    ws;
    userKey;
    party;
    mapType;
    mapSpot;
    maps = [];
    heartbeatInterval;
    heartbearTimeout;

    constructor(ws) {
        this.ws = ws;
        this.userKey = "";
        this.mapType = 0;
        this.mapSpot = 0;
        this.party = [];
        console.log("[Connection] New Connection")
        ws.on("error", (err) => this.OnError(err));
        ws.on("message", (msg) => this.OnMessage(msg));
        ws.on("close", () => this.OnClose());
        this.Heartbeat();
        this.heartbeatInterval = setInterval(() => this.Heartbeat(), 10000);
    }

    SendMessage(message) {
        if (this.IsClosed) return;
        let json = JSON.stringify(message);
        this.ws.send(json);
    }

    OnError(error) {
        if (this.IsClosed) return;
        console.error(error);
    }

    OnMessage(messageJson) {
        if (this.IsClosed) return;
        var message = JSON.parse(messageJson);

        if (!(
            "user" in message && typeof (message.user) === "string" && (message.user.length == 64 || message.user.length == 0) &&
            "party" in message && typeof (message.party) === "object" &&
            "mapType" in message && typeof (message.mapType) === "number" && message.mapType >= 0 && message.mapType <= 99 &&
            "mapSpot" in message && typeof (message.mapSpot) === "number" && message.mapSpot >= 0 && message.mapSpot <= 99 &&
            (!("maps" in message) || (
                Array.isArray(message.maps) &&
                message.maps.length <= 50 &&
                message.maps.every(m =>
                    m && typeof m === "object")
            )))) {
            console.error("Invald Data Message [1]");
            this.ws.close();
            return;
        }

        var pmCount = 0;

        for (const [p, v] of Object.entries(message.party)) {
            if (!p.length == 64) {
                console.error("Invalid Data Message [2]");
                this.ws.close();
                return;
            }
            pmCount++;

            if (pmCount > 7) {
                console.error("Invalid Data Message [3]");
                this.ws.close();
                return;
            }
        }

        this.userKey = message.user;

        if (this.userKey === "") {
            this.mapType = 0;
            this.mapSpot = 0;
            this.party = [];
            this.maps = [];
            return;
        }

        this.mapType = message.mapType;
        this.mapSpot = message.mapSpot;
        this.maps = message.maps ?? [];
        this.party = [];

        for (const [p, v] of Object.entries(message.party)) {
            this.party.push(p);
        }

        this.ForceHeartbeat();

        for (var i = 0; i < 7 && i < this.party.length; i++) {
            var find = Connections.filter(c => {
                return c.IsClosed === false && c.userKey === this.party[i] && c.party.includes(this.userKey);
            });

            for (var j = 0; j < find.length; j++) {
                find[j].ForceHeartbeat();
            }
        }
    }

    ForceHeartbeat() {
        clearTimeout(this.heartbearTimeout);
        clearInterval(this.heartbeatInterval);
        this.heartbearTimeout = setTimeout(() => this.Heartbeat(), 1000);
        this.heartbeatInterval = setInterval(() => this.Heartbeat(), 10000);
    }

    OnClose() {
        console.log("Disconnected");
        this.IsClosed = true;
        clearTimeout(this.heartbearTimeout);
        clearInterval(this.heartbeatInterval);
    }

    Heartbeat() {
        var party = {};
        for (var i = 0; i < 7 && i < this.party.length; i++) {
            var find = Connections.filter(c => {
                return c.IsClosed === false && c.userKey === this.party[i] && c.party.includes(this.userKey);
            });

            if (find.length == 0) {
                party[this.party[i]] = { mapType: 0, mapSpot: 0, maps: [] };
            } else {
                party[this.party[i]] = { mapType: find[0].mapType, mapSpot: find[0].mapSpot, maps: find[0].maps };
            }
        }

        this.SendMessage({
            user: this.userKey,
            mapType: this.mapType,
            mapSpot: this.mapSpot,
            maps: this.maps,
            party: party,
        })
    }
}


let Connections = [];
wss.on("connection", (ws) => {
    Connections.push(new ClientConnection(ws));
});

setInterval(() => {
    Connections = Connections.filter(c => c.IsClosed == false);
}, 30000);
