// Creates the UniFi Network Application DB users on first Mongo init.
// Must match MONGO_USER / MONGO_PASS / MONGO_DBNAME in compose.yml.
db.getSiblingDB("unifi").createUser({
  user: "unifi",
  pwd: "unifi-dev",
  roles: [{ role: "dbOwner", db: "unifi" }],
});
db.getSiblingDB("unifi_stat").createUser({
  user: "unifi",
  pwd: "unifi-dev",
  roles: [{ role: "dbOwner", db: "unifi_stat" }],
});
