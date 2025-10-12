# TODO List

Each subtitle has a priority number with 1 being the first one to be implemented.

## Proxy to Google images from MyMaps stopped working, Priority 1 URGENT

See wayfarer-20251011.log 2025-10-11 14:29:32.812 +00:00 [INF] start of a said request.

Also these requests/reposnses even when they where working they made the browser loading for a very long time;
When we fix the issue we should investigate if we can make them run on background/async or on similar manner as to not block page loading.

## Trusted Managers Mechanism, priority 3

Implement a trusted user/manager mechanism to allow managers see user location data.

### Implementation List

* Add necessary database mechanism to link managers with users, possible a many to many table
* Create the UI in user's settings to add/delete trusted manager(s)
* Implement the manager interface to select a user and see his location data.

## Geofencing, priority 3

### Implementation List

* Create the UI in both User and Manager areas to create and store geofence areas
* Implement geofence queries in User and Manager areas

## Manager Fleet Tracking System, priority 4

### Implementation List

* Create the UI in manager's area to allow managers track vehicle location data
* Create the necessary database mechanism to link managers with vehicles, a possible many to many table
* Design the system so that a manager can only add/remove vehicles from his organization which leads to:
* Managers and Vehicles should have an additional DB field Organization as a unique identifying field linking them
